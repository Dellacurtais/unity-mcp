# Auditoria do ambiente para agentes — Unity MCP / Unity 6.3

Data: 23/09/2026. Repositório: este repositório, branch `master`, HEAD `884abd9`, pacote `com.maicon.scene-analyzer@0.18.6`.

## Conclusão

O MCP tem uma base extensa e útil: autenticação local, descritores de ferramentas, capacidades, Undo, identidade de objetos, jobs e broker separado do Editor. A prioridade agora é garantir que projeto, alvo, execução e resultado sejam inequívocos, especialmente com vários agentes. Acrescentar ferramentas antes de corrigir esses contratos aumentaria a superfície de falhas.

Um ambiente controlado deve permitir autonomia dentro do escopo autorizado: cada operação pertence a uma sessão e projeto, declara seus efeitos, verifica precondições antes de escrever e produz um recibo verificável. Confirmações repetidas para operações já autorizadas não resolvem os defeitos de coordenação identificados.

Esta auditoria não modificou a implementação, configurações, cenas ou assets. Foi criado apenas este relatório. As propostas abaixo ainda precisam ser implementadas e validadas.

## Evidência e limites

| Verificação | Resultado nesta auditoria |
| --- | --- |
| Versão do projeto de jogo usado no teste | `ProjectSettings/ProjectVersion.txt`: `6000.3.5f2`, revisão `3fa8bc678cb0` |
| Pacote no checkout | `0.18.6`; descrição declara Unity 6000.1–6000.3 |
| Broker acessível por MCP | Sim: `broker_list_projects` e `broker_get_status` responderam |
| Editor conectado ao broker | Nenhum no momento da consulta: `connectedProjects=0`, `routeReady=false` |
| Projeto registrado mais recentemente | projeto de jogo, pacote 0.18.6, 335 ferramentas Unity; registro desconectado |
| Processos Unity | Havia processos Unity; processo existente não comprova bridge ou main thread disponível |
| Chamada ao Editor | `get_editor_context` não entregou resultado durante a espera; a espera da auditoria foi interrompida. Não foi possível determinar o estado da cena/compilação atual |
| Suíte Python do broker | **37 passed in 6.02s** |
| Contrato de release | `Tools~/validate_release.py`: passou |
| Contrato estático de modeling | `Tools~/validate_modeling_phase0.py`: passou |
| Compilação e testes Unity nesta auditoria | Não executados; sem comprovação ao vivo de EditMode/PlayMode |
| Provas adicionais do broker | Quatro comportamentos reproduzidos com SQLite temporário e conexões falsas, sem operar Unity |

Comando da suíte: `Broker~/.venv/Scripts/python.exe -B -m pytest Broker~/tests -q -p no:cacheprovider`. Os gates estáticos usaram o Python 3.12.14 do runtime local. A suíte broker usou o venv Python 3.14; isso não amplia automaticamente a matriz de suporte publicada.

## Correções prioritárias de confiabilidade

P1 significa corrigir antes de depender de execução concorrente/autônoma com escritas. P2 significa otimização ou endurecimento importante. Os achados C# foram confirmados por leitura dos caminhos de código; seus cenários ainda precisam de testes no Editor.

### P1 — Repetição de uma operação ainda em andamento pode duplicar a escrita

O ledger reconhece uma chave de idempotência existente, mas o broker só reaproveita o resultado se a requisição estiver terminal. Para estados em andamento, envia novamente o comando.

- Fonte: [mcp.py:141](../Broker~/mcp.py:141), despacho na linha 161.
- Reprodução isolada: duas chamadas não idempotentes, IDs JSON-RPC diferentes e mesma `idempotencyKey`, produziram **dois comandos com o mesmo ledger**.
- Impacto: uma reconexão/repetição pode criar ou alterar algo duas vezes. O bloqueio de mutações do Editor serializa a execução, mas não deduplica os efeitos.
- Correção: um único executor por `(projectKey, operationId)`; chamadas concorrentes recebem o mesmo recibo/job ou aguardam a mesma execução. Chave com argumentos diferentes deve continuar sendo rejeitada.
- Aceite: 20 repetições simultâneas produzem um efeito; repetir depois da conclusão devolve o mesmo resultado; queda após aceite não provoca replay cego. Não prometer execução exatamente uma vez após crash sem recibo durável também no lado Unity.

### P1 — Projeto ativo global e cancelamento sem vínculo com o proprietário

O despacho consulta o projeto ativo global. O cancelamento consulta o job, mas envia o comando ao projeto atualmente ativo; para refresh, marca o job cancelado antes de confirmar a ação no Editor.

- Fontes: [router.py:176](../Broker~/router.py:176), [router.py:246](../Broker~/router.py:246), [mcp.py:414](../Broker~/mcp.py:414).
- Reprodução isolada: job de refresh pertencente ao projeto A; B ativado; cancelamento enviado a B, enquanto o registro de A foi marcado `cancelled`.
- Correção: vincular projeto à sessão e capturá-lo na criação da operação. Despacho, espera, retomada e cancelamento usam esse vínculo imutável. A seleção visual global pode permanecer como conveniência para um único cliente.
- Aceite: alternar A/B durante chamadas e jobs não muda seus destinatários. `cancel_requested` só vira `cancelled` após confirmação ou prova de que a operação ainda não começou.

Também falta namespace de IDs por cliente: [router.py:182](../Broker~/router.py:182) encaminha o JSON-RPC original; [McpRequestProcessor.cs:334](../Editor/Mcp/McpRequestProcessor.cs:334) usa seu ID no coordenador global. Dois clientes com `id=1` podem colidir. Traduzir IDs externos para IDs internos únicos e manter a tradução para resposta/cancelamento.

### P1 — Reconexão é tratada como conclusão do trabalho

`_resume_refresh_jobs` seleciona todos os jobs `waiting_for_reconnect` do projeto e os marca como concluídos, sem filtrar o tipo ou comprovar resultado terminal.

- Fonte: [router.py:339](../Broker~/router.py:339).
- Reprodução isolada: registrar uma bridge em `compile_failed` promoveu jobs de refresh e teste para `completed`; o resultado interno do teste ainda dizia `Running`.
- Correção: reconciliação por tipo de job, geração e evidência. Refresh precisa comprovar término da importação/compilação; teste precisa recuperar o resultado do runner; build precisa do relatório correspondente. Reconexão muda disponibilidade, não o resultado do job.
- Aceite: uma bridge reconectada com erro de compilação nunca produz sucesso de refresh. Um teste interrompido fica em recuperação/resultado desconhecido até existir evidência terminal.

### P1 — Identidade forte pode cair silenciosamente para outro alvo

O resolver tenta GlobalObjectId/GUID e depois caminho. Um objeto excluído e recriado no mesmo caminho pode ser selecionado. Mesmo quando encontra um componente exato, ele volta a selecionar pelo índice entre componentes do mesmo tipo.

- Fonte: [ToolTargetResolver.cs:55](../Editor/Core/ToolTargetResolver.cs:55), seleção por ordinal na linha 74.
- Correção: se uma identidade forte fornecida não resolve, falhar com `TARGET_STALE`. Caminhos servem para localizar candidatos em uma recuperação explícita. Preservar o componente resolvido por GlobalObjectId; ordinal apenas no contrato legado.
- Aceite Unity: excluir/recriar um objeto no mesmo caminho não redireciona a escrita; remover o primeiro de três BoxColliders não faz uma referência ao segundo atingir o terceiro.

### P1 — Precondições são verificadas antes da espera pelo bloqueio

Resolução de alvo e autorização ocorrem antes de obter o lease de mutação. Durante a espera, permissões, Play Mode e objetos podem mudar.

- Fonte: [McpRequestProcessor.cs:285](../Editor/Mcp/McpRequestProcessor.cs:285); autorização na linha 307, lease na linha 336, execução na linha 359.
- Correção: repetir autorização, identidade e precondições na main thread após adquirir o lease e imediatamente antes da escrita. Acrescentar revisão esperada do recurso, geração do Editor e fingerprint do alvo quando aplicável.
- Aceite Unity: uma escrita enfileirada é rejeitada se, durante a espera, o usuário desabilita escritas, muda para Play Mode incompatível ou substitui o alvo.

### P1 — Atomicidade e cancelamento podem informar menos efeitos do que ocorreram

O changeset promete atomicidade, mas inclui handlers que salvam ScriptableObjects existentes imediatamente. O rollback desfaz Undo e apaga assets criados, sem restaurar explicitamente os bytes já persistidos de assets existentes.

- Fontes: [ApplyChangesetTool.cs:16](../Editor/Mcp/Tools/Write/ApplyChangesetTool.cs:16), [ScriptableObjectAuthor.cs:223](../Editor/Authoring/ScriptableObjectAuthor.cs:223), [ScriptableObjectAuthor.cs:370](../Editor/Authoring/ScriptableObjectAuthor.cs:370), [ChangesetExecutor.cs:204](../Editor/Authoring/ChangesetExecutor.cs:204).
- Correção: separar mutação e persistência, salvar somente no commit permitido e devolver `rollbackComplete` com eventuais falhas de compensação. Restringir o contrato atômico aos handlers que realmente o suportam.
- Aceite Unity: alterar SO existente, provocar falha posterior e comparar memória **e bytes reimportados** com o estado anterior.

Há outro problema de resultado: após executar uma ferramenta, a finalização ainda usa o token cancelável; se o prazo vence, um resultado conhecido pode ser substituído por `CANCELLED`.

- Fonte: [McpRequestProcessor.cs:359](../Editor/Mcp/McpRequestProcessor.cs:359), finalização nas linhas 363–378 e catch na 381.
- Correção: distinguir cancelado antes de executar, concluído, parcialmente aplicado e resultado desconhecido. Conservar o recibo já conhecido e concluir a invalidação de cache necessária com prazo próprio.
- Aceite Unity: uma mutação síncrona que termina após o deadline não deve induzir o agente a repeti-la por receber um cancelamento sem informações sobre efeitos.

### P2 — Prazo não envolve toda a transmissão; saúde não prova main thread responsiva

- Reprodução isolada: `dispatch(timeout=100ms)` continuou pendente após 500 ms quando o socket falso bloqueou em `send_json`. O timeout só envolve a espera posterior pela resposta.
- Fonte: [router.py:192](../Broker~/router.py:192), timeout na linha 215; default de 900 segundos em [config.py:26](../Broker~/config.py:26).
- Correção: deadline absoluto envolvendo fila, lock de envio, envio e resposta; cancelar/limpar os recursos pendentes em todos os caminhos. Leituras curtas devem ter orçamento menor; trabalhos longos devem retornar job rapidamente.
- Saúde: separar broker alcançável, WebSocket vivo e main thread responsiva. Incluir idade do último heartbeat e do último tick da main thread, fila, operação ativa e estados como `compiling`, `reloading`, `blocked` e `offline`.
- [MainThreadDispatcher.cs:24](../Editor/Mcp/MainThreadDispatcher.cs:24) limita o pump a 5 ms **entre closures**, sem interromper uma closure longa. Mover trabalhos divisíveis para jobs cooperativos; registrar os indivisíveis como não canceláveis após início.

## Otimizações medidas

As medições do catálogo e banco usaram o SQLite existente em modo somente leitura. O catálogo é um snapshot persistido, não prova de um Editor conectado. Não foram expostos conteúdos das requisições.

| Medição | Valor |
| --- | --- |
| Snapshot de catálogo consultado | `2026-09-23T15:52:17Z` |
| Ferramentas | 335 Unity + 8 broker = **343** |
| JSON compacto do catálogo | **661.652 bytes**, aproximadamente 646 KiB |
| Ferramentas Unity com outputSchema | 254 de 335 |
| Lookup de descriptor no código atual | Mediana **4,883 ms** em 100 consultas locais |
| Arquivo principal SQLite | **1.988.628.480 bytes**, aproximadamente 1,85 GiB |
| Tabela requests | 34.246 registros; aproximadamente **97,7%** do espaço alocado no banco |
| read_hierarchy no histórico | 375 chamadas; 657.799.805 caracteres de respostas persistidas; máximo 7.212.864 por resposta |
| screenshot_game_view no histórico | 715 chamadas; 420.466.266 caracteres de respostas persistidas |
| get_server_health no histórico | 5.326 chamadas; 133.272.753 caracteres de respostas persistidas |

O benchmark é de processamento local do catálogo, não latência end-to-end. Os agregados do histórico não medem tokens e não estabelecem, sozinhos, uma regressão de desempenho.

1. **Catálogo por perfil e busca.** Manter um conjunto inicial pequeno de conexão, contexto, inspeção, edição básica e jobs. Descobrir módulos de UI, rigging, modeling, AI3D e administração conforme a tarefa; preservar nomes existentes. Paginar `tools/list` e separar perfil de exposição de autorização real. Paginação isolada não reduz o total se o cliente buscar todas as páginas.
2. **Cache de descritores por versão.** [registry.py](../Broker~/registry.py) recompõe/serializa o catálogo no lookup. Calcular lista/hash/índice por nome quando a versão muda; lookup deve consultar o índice. Não invalidar schemas por mudanças normais na cena.
3. **Hierarquia limitada por escopo.** [ReadHierarchyTool.cs](../Editor/Mcp/Tools/Read/ReadHierarchyTool.cs) percorre o snapshot inteiro, sem filtro/limite no schema. Acrescentar cena/raiz, profundidade, componentes selecionados, cursor, limite de nós e orçamento de bytes. Devolver versão do snapshot e `truncated/nextCursor`.
4. **Resultados grandes como artefatos.** Devolver resumo estruturado e referência autenticada para imagens, hierarquias extensas e relatórios. O cliente deve poder recuperar o conteúdo quando necessário. Evitar copiar base64 para o ledger principal.
5. **Retenção explícita.** [store.py:313](../Broker~/store.py:313) persiste a resposta completa. Aplicar retenção por idade/tamanho para respostas de leitura e eventos; conservar recibos de mutações e tombstones de idempotência pelo prazo contratado. Expurgo não deve reabilitar uma escrita antiga por reutilização de chave. Não compactar/apagar o banco atual sem migração e backup apropriados.
6. **Saúde incremental e erros acionáveis.** Um status curto deve responder sem reemitir módulos e schemas. Erros devem incluir campo inválido, formato esperado, exemplos, `retrySafe`, efeitos conhecidos e próxima ação. Evitar polling frequente quando um wait de job/evento resolve.
7. **Contrato de mudança de catálogo.** O broker anuncia `listChanged=true`, mas o caminho de notificação não foi encontrado na implementação inspecionada. Implementar a notificação, ou anunciar a capacidade como ausente enquanto não suportada. A especificação define paginação, notificação de alteração e recursos referenciados: [MCP tools 2025-06-18](https://modelcontextprotocol.io/specification/2025-06-18/server/tools).
8. **Contratos tipados e paginação antes da materialização.** [ToolOutputContracts.cs:48](../Editor/Mcp/ToolOutputContracts.cs:48) usa contratos permissivos para 31 ferramentas: propriedades sem tipos e sem campos obrigatórios. Priorizar contratos reais para contexto, mutações, jobs e erros, verificando respostas em testes. [OperationalTools.cs:63](../Editor/Mcp/OperationalTools.cs:63) materializa os registros e schemas antes de paginar; aplicar paginação aos metadados antes de clonar schemas. Um `self_test` de estrutura não comprova a semântica de cada resultado.

Método das medições: abrir o banco com URI SQLite `mode=ro`; consultar o snapshot recente de `registry_snapshots`; montar o catálogo usando `RegistryCache` do checkout; medir 100 chamadas `descriptor(project, tools[-1]["name"])` com `time.perf_counter()`. O tamanho do catálogo usa JSON compacto UTF-8. Os agregados do histórico usam somente `COUNT`, `SUM(LENGTH(response_json))`, `MAX` e `dbstat`. O arquivo é ativo e pode mudar de tamanho após a medição.

## Ambiente proposto para os agentes

Fluxo: **vincular sessão/projeto → observar contexto → preparar alteração → verificar precondições → executar → verificar efeito → persistir conforme autorização**.

- **Sessão de trabalho:** `sessionId`, `agentId`, `projectKey`, identidade/geração do Editor, escopo autorizado e recursos reservados. Vários leitores são permitidos; escritas conflitantes têm um proprietário explícito.
- **Contexto inicial compacto:** Unity/pacote/pipeline, cenas e Prefab Stage ativos, dirty state, Play/compile/reload, capacidades, módulos disponíveis, revisão do contexto e operações pendentes.
- **Controle por recursos:** leases de AssetDatabase, UPM, build, Play Mode e cenas específicas. Um job mantém o lease necessário até terminar; terminar a request que criou o job não libera automaticamente esse recurso.
- **Escritas com precondições:** identidade forte, revisão esperada, operação deduplicável, recursos afetados e política de persistência. Rejeitar contexto desatualizado em vez de tentar adivinhar o alvo.
- **Recibo durável:** operação/job, projeto, geração, efeitos observados, assets/cenas alterados, persistência, Undo/compensação disponível, evidência, estado terminal e segurança de retry.
- **Observação e validação:** snapshots incrementais e console ancorado; para comportamento visual/runtime, screenshots e medições do caminho real em Play Mode continuam necessárias. Testes e assets configurados não comprovam o resultado visual.
- **Perfis de autonomia:** inspeção; autoria dentro das pastas/cenas autorizadas; testes/runtime; administração avançada. Reflection e menus arbitrários ficam num perfil de escape autorizado, sem serem apresentados como sandbox de efeitos.
- **Interação com o usuário:** reaproveitar a autorização da tarefa. Pedir intervenção quando muda o escopo ou existe uma decisão não autorizada, e apresentar a alteração concreta. Focus assist deve ser uma preferência explícita, pois o default atual pode trazer o Editor à frente depois de dois segundos.

Os jobs Unity também precisam de persistência uniforme: [ToolJobManager.cs:302](../Editor/Mcp/Jobs/ToolJobManager.cs:302) persiste apenas alguns tipos em SessionState. Reimports e outros jobs devem ter journal recuperável, respeitando as limitações da API Unity. Não foi demonstrada corrupção por concorrência; foi identificada ausência de uma garantia geral de exclusividade/recuperação.

## Unity 6.3 como alvo principal

Adotar **6000.3 como baseline certificado**, começando pelo patch instalado `6000.3.5f2`. Validar separadamente um patch 6.3 LTS atualizado antes de recomendar migração. Não há necessidade demonstrada de migrar para outra linha Unity para corrigir os problemas encontrados. A Unity identifica 6.3 como LTS: [política oficial de suporte](https://unity.com/releases/unity-6/support).

A CI atual em [.github/workflows/release-static.yml](../.github/workflows/release-static.yml) executa testes Python e parsing Roslyn; parsing não compila contra as APIs Unity nem executa EditMode/PlayMode. O push automático está limitado a `main`, mas o checkout está em `master`: alinhar o gatilho à branch de integração real, mantendo pull requests.

Matriz recomendada, em ordem de custo:

1. Contratos Python, schemas e release em todo PR.
2. Compilação e testes EditMode em projeto Unity 6000.3 mínimo e em projeto representativo com URP/Input System/Navigation/UI usados pelo usuário; isso também valida os gates e dependências opcionais.
3. PlayMode com Domain Reload e Scene Reload habilitados/desabilitados, verificando restauração de estado estático, subscriptions e bridge.
4. Testes de falha: desconexão antes/depois do aceite, crash do broker, reload durante job, timeout durante envio, dois agentes/projetos, IDs coincidentes, idempotência concorrente, alvo substituído e falha no meio de um changeset.
5. Smoke real de cliente MCP → broker → Editor, incluindo objeto criado/lido/desfeito, compile job e evidência visual de Play Mode num projeto de validação isolado.

A matriz Python atual é 3.10/3.12; o runtime instalado usa 3.14. Acrescentar a versão distribuída à CI ou padronizar o instalador numa versão já testada.

Desabilitar Domain Reload não deve ser tratado como otimização universal: muda as responsabilidades de reset de estado e eventos. A matriz deve cobrir ambas as configurações conforme a [documentação Unity 6000.3](https://docs.unity3d.com/6000.3/Documentation/Manual/domain-reloading.html).

## Ordem de entrega e critérios de conclusão

| Etapa | Entrega | Critério verificável |
| --- | --- | --- |
| 1 — Integridade | Deduplicação em andamento; projeto/ID por sessão; cancelamento correto; reconciliação de jobs; identidade forte | Provas adversariais viram testes que falham antes e passam depois; nenhum efeito duplicado ou sucesso inferido apenas da reconexão |
| 2 — Escrita controlada | Revalidação sob lease; estados de resultado honestos; persistência/rollback de changesets; leases de jobs | Mudança de contexto é rejeitada; falha intermediária demonstra o que foi restaurado em memória e disco |
| 3 — Eficiência | Perfis, índice/cache de catálogo, hierarquia limitada, artefatos e retenção | Medir redução de bytes por sessão e armazenamento; respeitar orçamento de resposta; não perder proteção de idempotência |
| 4 — Certificação 6.3 | CI Unity real, modos de reload, falhas e smoke de cliente | Evidência reproduzível no patch certificado e no conjunto de pacotes usado |

Metas iniciais propostas, ainda não medidas/atingidas: contexto padrão até 16 KiB; páginas comuns até 64 KiB sem mídia; leituras pequenas locais com p95 abaixo de 500 ms em Editor ocioso; despacho que cria job abaixo de 1 s quando o Editor está pronto; histórico mantido dentro de um orçamento configurável. Operações dependentes do projeto, importação ou APIs síncronas precisam de limites próprios e telemetria separada.

A entrega deve preservar compatibilidade dos nomes públicos e inputs legados, mas impedir fallback perigoso quando o chamador forneceu uma identidade forte. O objetivo é diminuir chamadas, ambiguidade e recuperação manual com garantias demonstráveis, sem prometer um ambiente infalível.

## Provas isoladas a transformar em regressões

Os probes importaram `BrokerStore`, `RegistryCache`, `BridgeRouter` e `McpBroker` do checkout, usando `TemporaryDirectory`, SQLite temporário e sockets falsos. Não usaram os endpoints ativos. Resumo das saídas:

```json
{"probe":"concurrent_idempotency","command_count":2,"same_ledger_id":true,"rpc_ids":[11,12]}
{"probe":"cancel_job_wrong_project","job_project":"a","cancel_target":"b","stored_state":"cancelled"}
{"probe":"reconnect_false_completion","bridge_state":"compile_failed","jobs":[{"kind":"refresh_compile","state":"completed","result":{"reconnected":true}},{"kind":"test","state":"completed","result":{"status":"Running","reconnected":true}}]}
{"dispatch_timeout_ms":100,"observed_after_ms":500,"task_still_pending":true}
```

Para repetir: registrar uma ferramenta não idempotente e executar duas `broker.process()` simultâneas com a mesma chave; registrar A/B e cancelar um refresh de A com B ativo; criar jobs em recuperação e registrar a bridge `compile_failed`; bloquear o `send_json` de um socket falso e observar um despacho com timeout de 100 ms por 500 ms. Cancelar e encerrar as tasks de teste ao final. Essas provas demonstram o transporte/orquestração; os cenários C# acima precisam de testes Unity próprios.
