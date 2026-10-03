# Quadro

Kanban em tempo real em C#, estilo Trello: quadros, colunas e cartões que você arrasta, com
várias pessoas mexendo ao mesmo tempo e cada mudança aparecendo na hora pra todo mundo.
Blazor Server na tela, SignalR no tempo real, EF Core com SQLite no banco.

A tela é a parte fácil. O difícil de um Kanban colaborativo é o que acontece quando duas pessoas
mexem no mesmo lugar no mesmo segundo, e é nisso que este projeto investe.

## O que tem de engenharia aqui

| Problema | Como foi resolvido | Onde |
|---|---|---|
| Guardar a ordem como 1, 2, 3 obriga a renumerar a coluna a cada movimento, e duas pessoas renumerando ao mesmo tempo bagunçam tudo. | **Índice fracionário**: a posição é um texto que se ordena sozinho. Entre "a" e "b" sempre cabe "aV". Mover grava só o cartão movido. | `Core/FractionalIndex.cs` |
| Inserir sempre no mesmo vão faz a posição crescer sem parar. | Quando passa de 24 caracteres, a coluna é **rebalanceada** na hora (posições curtas, mesma ordem, sem mudar versão de ninguém). | `BoardDecider.Rebalance` |
| Duas pessoas movem o mesmo cartão no mesmo instante. | **Concorrência otimista**: todo comando leva a versão do cartão que a pessoa viu. Uma ganha, a outra recebe "conflito" com o cartão atual, sem sobrescrever nada. | `Core/BoardDecider.cs` |
| Depois do conflito, salvar de novo apagaria em silêncio o que a outra pessoa escreveu. | **Merge de três vias** campo a campo: o que você não mexeu vem da versão nova, o que só você mexeu fica seu, e se os dois mexeram no mesmo campo o app aponta qual. Esse caso foi achado testando com duas pessoas no navegador. | `Core/CardMerge.cs` |
| A tela pede "solta depois do cartão X", mas X pode ter saído da coluna um instante antes. | O comando diz **entre quais cartões** soltar, não a posição; o servidor calcula com o quadro atualizado e ignora vizinho que sumiu. | `DecideMove` |
| Operações chegando fora de ordem ou duplicadas deixariam as telas diferentes. | **Uma fila por quadro**: cada operação ganha um número de sequência sem buraco, é gravada no banco e só então sai pra todo mundo, na mesma ordem. Operação repetida é ignorada. | `Web/Realtime/BoardEngine.cs` |
| Quem cai e volta não pode ficar com o quadro velho. | O cliente informa a última sequência que viu e recebe **só o que perdeu** (até 500 operações; mais que isso, a foto completa). | `BoardHub.Join` |
| Cliente mal-intencionado mandando "fui eu, o Chefe". | Quem fez a ação é o nome registrado na conexão, nunca o que vem no comando. | `BoardHub.Run` |
| Kanban sem limite vira lista de tarefas. | **Limite de WIP** por coluna: não entra cartão novo nem chega cartão de outra coluna quando está cheia (reordenar dentro dela pode). | `WipFull` |
| Arrastar não funciona no celular nem no teclado. | Editor com "mover para" e botões ↑ ↓ como alternativa ao arrastar. | `Board.razor` |

**Log de operações:** tudo que acontece no quadro fica gravado em ordem (tabela `Ops`). Dá pra
reconstruir o quadro inteiro só com o log, e um teste prova que o resultado é idêntico ao das
tabelas.

## Arquitetura

```
src/
  Quadro.Core/      núcleo funcional, sem banco nem rede
    FractionalIndex   posição dos cartões
    BoardDecider      Decide(estado, comando) -> operação  |  Apply(estado, operação) -> estado
    CardMerge         junta edições concorrentes
  Quadro.Web/       Blazor Server + SignalR + EF Core (SQLite)
    BoardEngine       fila por quadro, grava a operação e avisa todo mundo
    BoardHub          /hubs/board: porta SignalR pra qualquer cliente (app, script, integração)
    Pages/Board       a tela: arrastar e soltar, editor, quem está online, atividade ao vivo
tests/
  Quadro.Tests/     41 testes: núcleo + integração com o servidor de verdade
```

A tela em Blazor Server já conversa com o navegador por SignalR (é assim que o Blazor Server
funciona) e usa o `BoardEngine` direto. O hub `/hubs/board` expõe o mesmo quadro pra outros
clientes. As duas portas recebem as mesmas operações, na mesma ordem.

## Testes

```bash
dotnet test
```

Os testes de integração sobem o app de verdade em memória, com SQLite num arquivo temporário, e
conectam clientes SignalR reais:

- o que uma pessoa faz chega na outra com o mesmo número de sequência;
- duas pessoas movendo o mesmo cartão ao mesmo tempo: exatamente uma ganha, a outra recebe conflito;
- 40 cartões criados em paralelo por 4 pessoas saem com sequência contínua e posições únicas;
- quem reconecta recebe só as operações perdidas, em ordem;
- ninguém consegue agir em nome de outra pessoa;
- o log reconstrói o quadro, e o quadro sobrevive a derrubar e subir o app de novo.

Toda a regra, o servidor e a tela são C#, sem nenhum arquivo JavaScript próprio. Fora do C# só
tem o CSS do visual.

## Rodar

Precisa do .NET 8 SDK.

```bash
dotnet run --project src/Quadro.Web
```

Abre num quadro de exemplo. Pra ver o tempo real, abra em duas janelas com nomes diferentes.
O banco é um arquivo `quadro.db` (configurável em `ConnectionStrings:Quadro`).

## Decisões assumidas

- **Sem login.** A pessoa só diz o nome, guardado no navegador. Pra uso real, o próximo passo é
  ASP.NET Core Identity; o resto não muda, porque o nome já vem da conexão e não do comando.
- **Um servidor só.** A fila por quadro vive na memória do processo. Pra rodar em várias
  instâncias, a fila precisaria virar distribuída (Redis, por exemplo) e o SignalR precisaria de
  backplane. A versão do cartão no banco já funciona como segunda trava pra esse cenário.
- **Um quadro de exemplo.** O modelo e o hub já são por quadro (`boardId`); falta a tela de
  criar e listar quadros.

## Licença

MIT
