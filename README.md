# KeyValueStore em C#

Armazenamento chave-valor durável em C# (.NET 8), baseado em **write-ahead log (WAL)**. Toda escrita é anexada a um log com checksum antes de alterar a memória; ao abrir, o log é reproduzido e qualquer cauda corrompida (por exemplo, uma escrita interrompida por uma queda) é detectada e descartada. Inclui escritas em lote atômicas, compactação do log e uma CLI.

## Por que este projeto

Bancos de dados, filas e sistemas de arquivos compartilham a mesma ideia: registrar a intenção em um log sequencial e durável antes de aplicá-la (PostgreSQL, RocksDB, Kafka, etcd, Bitcask). Entender WAL, `fsync`, recuperação de falhas e compactação é conteúdo central de entrevistas de infraestrutura e de sistemas distribuídos. Este projeto implementa o ciclo completo em poucas centenas de linhas, com testes que simulam arquivos truncados e corrompidos.

## Recursos

- `Put`, `Get`, `Delete`, `ContainsKey`, `Keys`, `Stats`, com chaves `string` (UTF-8) e valores binários.
- **Log com CRC32** por registro e formato versionado por *magic number* (`KVL1`).
- **Recuperação de falhas:** registros incompletos ou com CRC inválido no fim do arquivo são truncados; `RecoveryInfo` informa quantos registros foram reproduzidos e quantos bytes foram descartados.
- **Lotes atômicos:** `Write(b => { b.Put(...); b.Delete(...); })` grava um único registro; após uma queda, todas as operações do lote aparecem ou nenhuma.
- **Compactação** manual ou automática (por tamanho mínimo e razão de lixo), com escrita em arquivo temporário, `fsync` e renomeação atômica.
- Durabilidade configurável: `SyncMode.Always` (`fsync` a cada escrita) ou `SyncMode.OnClose`.
- CLI com comandos `set`, `get`, `del`, `keys`, `stats`, `compact`.

## Como executar

Pré-requisito: .NET 8 SDK.

```bash
dotnet build
dotnet run --project src/KeyValueStore.Cli -- dados.log set nome Thiago
dotnet run --project src/KeyValueStore.Cli -- dados.log get nome
dotnet run --project src/KeyValueStore.Cli -- dados.log stats
dotnet run --project src/KeyValueStore.Cli -- dados.log compact
```

Sem comando, a CLI lê comandos da entrada padrão (modo interativo):

```bash
printf 'set a 1\nget a\nkeys\n' | dotnet run --project src/KeyValueStore.Cli -- dados.log
```

Como biblioteca:

```csharp
using var store = KvStore.Open("dados.log");
store.Put("usuario:1", "ana");
store.Write(b => { b.Put("x", "1"); b.Put("y", "2"); });
```

## Como testar

```bash
dotnet test
```

Os testes truncam o arquivo no meio do último registro, invertem bits para quebrar o CRC, anexam lixo ao fim, deixam um arquivo `.compact` órfão, verificam a atomicidade de lotes após truncamento, a redução de tamanho após compactação e escritas concorrentes de 8 threads. Há também o vetor de teste padrão do CRC32 (`123456789` resulta em `0xCBF43926`).

## Decisões de projeto

- **Formato do registro:** `crc32(payload) | tamanho | payload`, e o payload contém a contagem de operações seguida de cada operação. Como toda mutação é um registro, o registro é a unidade de atomicidade e o mesmo código serve para escritas simples e em lote.
- **Dados em memória, log como fonte da verdade.** Leituras são O(1) (dicionário); escritas são um *append* sequencial. A recuperação é O(tamanho do log). O limite é a RAM, ao contrário de um Bitcask real, que mantém só o índice em memória.
- **Truncar no primeiro registro inválido.** Em um WAL de um único arquivo, uma cauda corrompida e uma escrita interrompida são indistinguíveis; descartamos tudo a partir dali. Isso preserva a consistência (nunca se aplica um registro parcial) ao custo de, em caso de corrupção no meio do arquivo, perder também os registros seguintes.
- **Escrita antes da memória.** Se o `Write` ou o `fsync` falharem, o log é revertido ao tamanho anterior e o estado em memória não muda.
- **Compactação atômica:** novo log em `arquivo.compact` com `fsync`, seguido de `File.Move` com sobrescrita (rename atômico no POSIX). Uma queda deixa o log antigo ou o novo, nunca um meio-termo; um `.compact` órfão é apagado na abertura.
- **Um lock global** serializa operações. Simples e correto; um store de produção usaria *group commit* para amortizar o custo do `fsync` entre escritores concorrentes.

## Estrutura de pastas

```
KeyValueStore/
├── KeyValueStore.sln
├── src/
│   ├── KeyValueStore/       # KvStore, formato do log (LogFormat), opções
│   └── KeyValueStore.Cli/   # CLI
└── tests/
    └── KeyValueStore.Tests/ # testes xUnit
```

## Limitações conhecidas

- Todo o conjunto de dados precisa caber na memória; a compactação também reescreve tudo (bloqueando escritas).
- Sem *group commit*: com `SyncMode.Always`, cada escrita paga um `fsync`.
- Sem checksum de blocos para detectar corrupção silenciosa no meio do arquivo (apenas por registro, tratada como fim do log).
- Sem acesso por múltiplos processos (o arquivo é aberto com `FileShare.Read`), sem iteração por intervalo, TTL ou snapshots.
- `fsync` em diretório após o rename não é feito; em alguns sistemas de arquivos isso é necessário para durabilidade total do rename.

## Licença

MIT. Veja o arquivo [LICENSE](LICENSE).
