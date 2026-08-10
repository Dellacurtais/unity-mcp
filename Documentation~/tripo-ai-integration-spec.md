# Tripo AI Provider, Unity Asset Pipeline e MCP — especificação normativa

> Versão do contrato: `1.2-draft`
> Baseline preservada: tag `v0.18.2`, commit `e818b60bf80934eef945c2fe15422459bc4d19f1`  
> Escopo inicial: Editor Unity, gateway local/servidor, Tripo API v3 e futura superfície MCP `ai3d_*`

## 1. Objetivo

Integrar a Tripo como provedor externo de geração e processamento 3D sem criar um caminho paralelo de mutação. A Tripo produz candidatos; Unity continua sendo a fonte autoritativa para importação, edição, rig, validação, preview, confirmação e publicação.

O agente deve poder solicitar um asset em linguagem natural, acompanhar a tarefa remota, inspecionar os candidatos e aplicar as ferramentas existentes do Asset Modeler e Rig & Animation Studio. Nenhum candidato entra em `Assets/` ou na cena antes de passar pela quarentena, pelos validadores locais e por uma aprovação ligada a hashes.

## 2. Princípios invariantes

1. A chave Tripo nunca aparece em assets Unity, argumentos MCP, logs, screenshots, manifests ou respostas HTTP.
2. Planejar é separado de gastar créditos. Ações faturáveis exigem orçamento, consentimento de envio e idempotência.
3. Texto, imagens e meshes têm permissões de envio independentes.
4. Downloads remotos entram primeiro em um artifact vault fora de `Assets/`.
5. URLs temporárias são baixadas e hashadas imediatamente depois do sucesso.
6. O FBX/GLB remoto nunca substitui silenciosamente o asset importado de origem.
7. Retopologia e segmentação remotas criam variantes; preservação de skinning ou blend shapes só é declarada depois de validação local.
8. UI, Coder Chat e MCP chamam os mesmos serviços de workflow.
9. O agente nunca declara sucesso somente porque a tarefa Tripo terminou.
10. Toda aceitação futura requer `planHash`, `candidateHash`, `validationHash` e `previewHash` compatíveis.

## 3. Limites históricos das Fases 0 e 1

As Fases 0 e 1 implementaram contratos e infraestrutura remota. Naquele corte elas não:

- importam automaticamente um resultado na Unity;
- adicionam ainda a janela AI 3D Studio;
- registram ainda as ferramentas MCP `ai3d_*`;
- gastam créditos em testes automatizados;
- prometem cancelamento remoto quando o endpoint não está documentado pela Tripo;
- tratam o Tripo MCP alpha/Blender como dependência de produção.

## 4. Arquitetura

```text
Coder Chat / agente              Unity AI 3D Studio (fase 2)
          |                                  |
          +---------- ai3d workflow --------+
                             |
                    Tripo Gateway v1.2
                    - segredo e auth local
                    - políticas e orçamento
                    - idempotência
                    - jobs duráveis
                    - polling/backoff
                    - webhook HMAC/dedupe
                    - quotas por projeto
                    - telemetria redigida
                    - upload aprovado
                    - artifact vault
                             |
                       Tripo API v3
                             |
                    candidato em quarentena
                             |
                Unity Import Adapter (fase 2)
                             |
              Asset Modeler / Rig Studio / validators
```

### 4.1 Fronteira do provedor

O gateway expõe um contrato estável e mantém os detalhes da Tripo em `TripoHttpProvider`. O restante do produto depende de uma interface de provedor com:

- `get_balance`;
- `get_usage`;
- `upload_file`;
- `create_task`;
- `get_task`;
- download validado de outputs.

Endpoints remotos são escolhidos por uma allowlist de operações tipadas. O gateway não aceita URL ou caminho HTTP arbitrário enviado pelo agente.

### 4.2 Operações Tripo permitidas

| Operação interna | Endpoint v3 |
|---|---|
| `text_to_model` | `/generation/text-to-model` |
| `image_to_model` | `/generation/image-to-model` |
| `multiview_to_model` | `/generation/multiview-to-model` |
| `texture` | `/models/texture` |
| `convert` | `/models/convert` |
| `segment` | `/mesh/segment` |
| `complete` | `/mesh/complete` |
| `retopology` | `/mesh/decimate` |
| `rig_check` | `/animations/rig-check` |
| `rig` | `/animations/rig` |
| `retarget` | `/animations/retarget` |

O catálogo deve ser versionado. Versões de modelos e parâmetros não são inventados pelo gateway; são recebidos de um plano revisado.

## 5. Segurança

### 5.1 Credenciais

- credencial Tripo: obrigatória para chamadas reais, lida somente pelo processo do gateway e obtida do Windows Credential Manager ou do fallback `TRIPO_API_KEY`.
- `TRIPO_GATEWAY_TOKEN`: autentica Unity/MCP no gateway local.
- `TRIPO_WEBHOOK_SECRET`: signing secret `whsec_…`, exclusivo do gateway, usado para validar `{timestamp}.{raw_body}` com HMAC-SHA256 e janela anti-replay.
- respostas de health indicam apenas `configured: true|false`.
- exceções e logs passam por redaction antes de serem persistidos.

O serviço inicia em loopback. Bind não-loopback deve ser recusado no launcher padrão até existir TLS, autenticação forte e política de rede explícita.

No Windows, a janela AI 3D Studio pode enviar a key uma única vez ao endpoint autenticado do gateway. O gateway persiste a credencial genérica no Windows Credential Manager e troca a key efetiva em memória sem reiniciar. A Unity mantém apenas o texto temporário do campo até a conclusão da chamada, limpa o JSON/buffer de envio e nunca escreve a key em `Assets`, `EditorPrefs`, manifests ou MCP. Substituição, teste e remoção continuam explícitos. Em plataformas sem secret store suportado, `TRIPO_API_KEY` é o fallback.

### 5.2 Upload local

Um upload por path exige:

- `external_send_approved: true`;
- path sob uma das raízes de `TRIPO_GATEWAY_UPLOAD_ROOTS`;
- extensão permitida;
- tamanho dentro do limite do tipo;
- `expected_sha256` compatível;
- registro do hash, tamanho e nome, sem duplicar o conteúdo no job JSON.

Imagens permitidas: JPEG e PNG, até 20 MB. Modelos permitidos: GLB, GLTF, FBX, OBJ e STL, até 150 MB.

### 5.3 Download remoto

- somente HTTPS;
- host deve terminar em `.tripo3d.ai` ou `.tripo3d.com`;
- userinfo, fragments e portas não padrão são recusados;
- streaming com limite configurável;
- arquivo temporário `.part`, `fsync`, hash SHA-256 e rename atômico;
- nomes remotos nunca são usados diretamente como path;
- MIME, extensão e magic bytes serão novamente validados pelo Unity Import Adapter.

## 6. Jobs duráveis

Cada job é um JSON atômico em `<data-dir>/jobs/<jobId>.json`. O artifact vault fica em `<data-dir>/artifacts/<jobId>/`.

Estados locais:

```text
queued
submitting
polling
downloading
completed
failed
cancel_requested
cancelled
completed_after_cancel
interrupted
```

Campos obrigatórios:

```text
schemaVersion
jobId
projectKey
operation
planHash
requestHash
idempotencyKeyHash
providerTaskId
status/progress/message
budget estimate/cap
creditsConsumed
safeInput
providerResult
artifacts[]
cancelRequested
created/updated/completed UTC
error { code, message, requestId }
```

Ao reiniciar, jobs não terminais com `providerTaskId` voltam ao polling. Job interrompido antes de receber `providerTaskId` falha com erro recuperável, pois ressubmeter sem uma idempotência confirmada no provedor poderia cobrar duas vezes.

### 6.1 Idempotência

`POST /v1/jobs` exige `Idempotency-Key`.

- mesma key + mesmo request hash retorna o job existente;
- mesma key + request diferente retorna `409 IDEMPOTENCY_CONFLICT`;
- a key bruta não é persistida, somente SHA-256;
- o request hash usa JSON canônico.

### 6.2 Cancelamento

Enquanto não houver endpoint v3 oficial confirmado:

- antes da submissão: cancela sem chamada externa;
- depois da submissão: marca `cancel_requested`, continua reconciliando em baixa frequência e não baixa artifacts;
- sucesso posterior vira `completed_after_cancel` e registra créditos consumidos;
- status remoto `cancelled` vira `cancelled`.

A UI não deve prometer reembolso apenas porque o cancelamento local foi solicitado.

## 7. Orçamento e cobrança

O gateway consulta saldo antes de uma ação faturável. Cada job traz:

- `estimatedCredits`;
- `budgetCapCredits`;
- `externalSendApproved`.

Regras:

1. `estimatedCredits <= budgetCapCredits`;
2. `budgetCapCredits <= TRIPO_GATEWAY_MAX_CREDITS_PER_JOB`;
3. saldo disponível deve ser pelo menos `estimatedCredits`;
4. o job para antes do próximo nó de um workflow quando o orçamento restante for insuficiente;
5. `credits_consumed` retornado pela Tripo é a fonte factual pós-execução.

O limite local não é um limite transacional imposto pela Tripo. Por isso, custos desconhecidos serão bloqueados no futuro planner, salvo autorização explícita de política administrativa.

## 8. Contrato HTTP local v1

| Método | Rota | Semântica |
|---|---|---|
| GET | `/health` | readiness sem segredo |
| GET | `/v1/capabilities` | operações, limites e cancelamento remoto disponível |
| GET | `/v1/account/balance` | saldo/frozen |
| GET | `/v1/account/usage` | uso recente limitado |
| GET | `/v1/credentials/tripo` | estado seguro da credencial, sem retornar a key |
| PUT | `/v1/credentials/tripo` | substitui a key no secret store autenticado |
| DELETE | `/v1/credentials/tripo` | remove a key persistida; env fallback pode permanecer |
| POST | `/v1/uploads` | upload aprovado por path/hash |
| POST | `/v1/jobs` | cria job assíncrono idempotente |
| GET | `/v1/jobs` | lista resumida e paginada |
| GET | `/v1/jobs/{jobId}` | estado completo seguro |
| POST | `/v1/jobs/{jobId}/cancel` | solicita cancelamento cooperativo |

Todas as rotas `/v1/*` exigem bearer token local. `/health` não exige token e nunca revela credenciais.

`/v1/capabilities` também retorna `artifactVaultRoot` apenas ao cliente autenticado. O Unity Import Adapter exige que cada artifact esteja sob `<artifactVaultRoot>/<jobId>/`; um path arbitrário, ainda que aponte para um FBX válido, é bloqueado.

## 9. Artifact vault e proveniência

Cada artifact registra:

- logical name (`model`, `rendered_image`, etc.);
- path absoluto local;
- byte count;
- SHA-256;
- content type;
- source host;
- download UTC.

O job guarda o output remoto para auditoria, mas a etapa Unity usa somente artifacts locais já hashados. O Import Adapter futuro recusará path fora do vault e hash divergente.

## 10. Integração Unity planejada

### 10.1 Formatos

O corte inicial usa FBX gerado por `/models/convert`, pois o pacote não possui importador GLB obrigatório. Um adapter opcional para glTFast pode ser introduzido depois.

O ModelImporter Tripo deve configurar antes da inspeção:

- Read/Write;
- normals/tangents por preset;
- materiais e extração de texturas;
- escala, eixo e pivot explícitos;
- Humanoid/Generic quando aplicável;
- animações e root motion conforme o workflow.

### 10.2 Gates locais

Todo candidato executará, conforme categoria:

- `mesh_validate` e validação topológica estendida;
- bounds, escala, pivot e orientação;
- submeshes, materiais, UVs, normals e tangents;
- triangle/vertex budget;
- screenshots multiview e silhouette A/B;
- `rig_inspect` e `rig_validate_clips` para skinned assets;
- transferência de pesos e bind poses após retopologia de mesh rigada;
- Surface Fit e clearance multipose para roupas;
- Smart Body Split local para modularidade exata.

## 11. Superfície MCP planejada

```text
ai3d_list_providers
ai3d_plan_workflow
ai3d_start_workflow
ai3d_get_workflow
ai3d_wait_workflow
ai3d_cancel_workflow
ai3d_preview_candidate
ai3d_validate_candidate
ai3d_accept_candidate
ai3d_reject_candidate
```

Não haverá `tripo_call_endpoint`. Ferramentas de baixo nível ficam internas ao provider adapter.

## 12. Presets planejados

- `game-mobile`: baixo orçamento geométrico, textura compacta e FBX.
- `game-pc`: 10–20k faces por asset simples, PBR e FBX.
- `hero-prop`: geração high fidelity seguida de retopologia A/B.
- `character-humanoid`: generate → retopo → rig-check → Mixamo rig → FBX.
- `character-creature`: rig type recomendado pelo rig-check.
- `modular-character`: segmentação como proposta, split exato local quando rigado.
- `wearable`: generate/process → Surface Fit → weight transfer → multipose.
- `animation-pack`: rig → retarget → Clip Studio/IK/bake local.

## 13. Observabilidade

Logs estruturados contêm apenas:

- correlation/job/provider task IDs;
- operação e estágio;
- duração, progresso e tentativas;
- rate-limit headers;
- créditos estimados/consumidos;
- hashes e artifacts;
- códigos de erro e `request_id`.

Prompts completos, tokens, headers de autorização e conteúdo de arquivos não entram em logs.

## 14. Testes

### 14.1 Fase 0

- schemas Pydantic rejeitam campos extras;
- fake provider reproduz queued/running/success/failure;
- fixtures representam balance, task success, task failure e 429;
- JSON canônico e hashes são determinísticos;
- API key nunca aparece em health ou serialização de job.

### 14.2 Fase 1

- autenticação local obrigatória;
- allowlist de operações/endpoints;
- retry respeita `Retry-After`;
- idempotência sobrevive a restart;
- job submetido sobrevive a restart e retoma polling;
- artifact download é atômico, limitado e hashado;
- host remoto não aprovado é recusado;
- upload fora das raízes ou com hash divergente é recusado;
- cancelamento pós-submit reconcilia sem prometer cancelamento remoto;
- nenhum teste automatizado consome créditos reais.

### 14.3 Teste real opt-in

O primeiro smoke real permitido é `GET /account/balance`. Uma geração real só ocorre com key configurada, orçamento explícito e confirmação do usuário. O resultado e os créditos consumidos devem ser anexados ao relatório da fase, nunca às fixtures versionadas.

## 15. Fases e gates

| Fase | Escopo | Gate |
|---|---|---|
| 0 | baseline/tag, spec, contratos, fixtures e fake provider | nenhum segredo ou crédito; testes determinísticos aprovados |
| 1 | gateway, auth, account, upload, jobs, polling, retry e vault | restart/idempotência/download seguro aprovados |
| 2 | cliente e AI 3D Studio na Unity, FBX quarantine/import | texto/imagem → preview local sem mutação automática |
| 3 | `ai3d_*`, policy de agente, budget e confirmação | agente planeja/executa sem endpoint cru |
| 4 | Smart Low Poly/Retopology e comparação A/B | budget geométrico e silhouette comprovados |
| 5 | segmentação/completion e Smart Body Split | rig/canais não são perdidos silenciosamente |
| 6 | rig/retarget/animação Tripo + corretores locais | clips e deformação aprovados em assets reais |
| 7 | webhooks, quotas, telemetria, CI e release | matriz Unity e prova visual real concluídas |

## 16. Estado de acompanhamento

- [x] Baseline `v0.18.2` preservada.
- [x] Fase 0 concluída: contrato, fixtures, fake provider e hashes determinísticos.
- [x] Fase 1 concluída: gateway, autenticação, account, upload, jobs, restart, retry e vault.
- [ ] Smoke real de balance executado com credencial do usuário.
- [x] Fase 2 concluída em código: cliente Unity, AI 3D Studio, FBX quarantine/import, preview e aceitação explícita.
- [x] Fase 3 concluída em código: domínio compartilhado, dez ferramentas `ai3d_*`, policy de agente, orçamento, consentimento e confirmação.
- [x] AI 3D Studio migrou sua geração principal para o mesmo planner/runner usado pelo MCP, incluindo conversão FBX automática dentro do limite aprovado.
- [x] Fase 4 concluída em código e testes: workflow `retopology`, limites oficiais v1/v2, orçamento por nó, relatório geométrico A/B e preview fonte/candidato lado a lado.
- [ ] Gate visual real da Fase 4: screenshot/silhouette de um asset Tripo real ainda precisa ser aprovado na Unity ativa.
- [x] Fase 5 concluída em código e testes: `segment_complete` v1/v2, granularidade, connectivity split, `quick_cap`/`ai_completion` e auditoria local de skin weights, bind poses e blend shapes.
- [ ] Gate real da Fase 5: validar fechamento e preservação de canais em pelo menos um personagem rigado real; perdas são bloqueadas e encaminhadas ao Smart Body Split local.
- [x] Fase 6 concluída em código e testes: cadeia durável convert GLB → rig-check → rig → retarget FBX, rig type recomendado, lote de até cinco presets e Rig Inspector/clip validation locais no candidato.
- [ ] Gate real da Fase 6: aprovar deformação e clips de um humanoide e de uma criatura reais na Unity ativa.
- [x] Fase 7 concluída em infraestrutura: webhook Tripo assinado/deduplicado, reconciliação sem polling duplicado, quotas diária/concorrrente por projeto, telemetria redigida e CI de contratos sem créditos.
- [ ] Gate final da Fase 7: matriz visual nas versões Unity suportadas, webhook HTTPS real e smoke/release com credencial do usuário.
- [ ] Inventário `ai3d_*` verificado em um cliente MCP externo depois de instalar/recarregar esta revisão no projeto ativo.
- [ ] Smoke real Tripo e prova visual com um FBX do provedor executados com credencial do usuário.

### 16.1 Evidência da entrega

- tag anotada `v0.18.2` aponta para `e818b60bf80934eef945c2fe15422459bc4d19f1`;
- `python -m pytest TripoGateway~/tests -q`: 33 testes aprovados;
- `python Tools~/validate_release.py`: contrato de release aprovado;
- `python Tools~/validate_modeling_phase0.py`: gate estático do Asset Modeler aprovado;
- smoke HTTP real em loopback: health e capabilities aprovados, 11 operações allowlisted, cancelamento remoto desativado;
- `TRIPO_API_KEY` não estava configurada nesta máquina, portanto nenhuma chamada Tripo real ou consumo de crédito ocorreu.

### 16.2 Evidência da Fase 2

- assembly Editor isolada `com.maicon.scene-analyzer.Editor.AI3D`, sem alterar a superfície MCP existente;
- AI 3D Studio com geração texto/imagem, presets explícitos, consentimento, orçamento, jobs, polling, cancelamento honesto, conversão FBX e saldo;
- depois do handoff seguro, a credencial efetiva permanece exclusiva do gateway; Unity persiste somente o token local e recusa URLs fora de loopback HTTP;
- adapter recusa artifact fora do vault/job autenticado, status não concluído, host não aprovado, tamanho/hash/magic divergentes e `planHash` inválido;
- importação é somente para quarentena, usa `ModelImporter`, `MeshValidator`, métricas e manifest; bytes importados são rehashados antes da promoção;
- preview desenha o asset diretamente no Scene View sem criar GameObject; mover/rotacionar/escalar afetam somente o preview;
- aceitação exige `planHash`, `candidateHash`, `validationHash` e `previewHash` atuais; instanciar na cena é uma ação posterior, explícita e Undo-aware;
- Unity 6000.3.5f2, projeto isolado: `Maicon.SceneAnalyzer.Editor.AI3D.Tests` aprovou 7/7 e compilou todos os assemblies do pacote;
- gateway/fake provider: 30/30 testes aprovados; release gate e Asset Modeler static gate aprovados;
- Windows Credential Manager: round-trip nativo write/read/delete aprovado com credencial sintética temporária e limpeza confirmada;
- a Unity ativa do ZombieWave permaneceu em `0.18.1`; nenhum manifest, asset ou cena do jogo foi alterado durante esta validação.

### 16.3 Evidência da Fase 3

- superfície MCP tipada: `ai3d_list_providers`, `ai3d_plan_workflow`, `ai3d_start_workflow`, `ai3d_get_workflow`, `ai3d_wait_workflow`, `ai3d_cancel_workflow`, `ai3d_preview_candidate`, `ai3d_validate_candidate`, `ai3d_accept_candidate` e `ai3d_reject_candidate`; não existe ferramenta de endpoint cru;
- `ai3d_plan_workflow` é local e não faturável, não envia dados, não retorna o prompt completo e produz `workflowId`, `planHash`, disclosure, nós, estimativa e limite revisáveis;
- `ExternalService` é uma capacidade MCP separada, desativada por padrão; início e cancelamento pagos sempre exigem confirmação de uso único mesmo quando a confirmação global de alto risco está desligada;
- o início exige correspondência exata de `planHash`, budget aprovado e consentimento independente de texto/imagem/mesh antes da consulta de saldo ou submissão;
- cada nó usa uma idempotency key determinística; geração e conversão compartilham o mesmo `planHash`, e o runner bloqueia o próximo nó se o orçamento restante não cobrir seu cap;
- estado durável fica em `Library/com.maicon.scene-analyzer/ai3d/workflows`, com escrita atômica; upload aprovado recebe handoff durável para não ser repetido após falha, e o payload de retomada é removido depois que o primeiro job se torna durável; prompt e paths de retomada nunca aparecem na resposta MCP pública;
- validação reconsulta o job autenticado e o vault, rehasha o FBX, importa somente para quarentena e registra issues; preview não cria GameObject; aceitação compara `planHash`, `candidateHash`, `validationHash` e `previewHash` e não instancia nada na cena;
- rejeição não apaga silenciosamente o artifact ou a quarentena; cancelamento não promete cancelamento remoto nem reembolso;
- os presets executáveis incluem `game-mobile`, `game-pc`, `hero-prop`, `character-humanoid`, `character-creature`, `modular-character` e `animation-pack`; `wearable` continua bloqueado até Surface Fit e transferência de pesos integrarem o workflow remoto;
- Unity 6000.3.5f2, projeto isolado: AI3D EditMode aprovou 15/15; contrato MCP aprovou 41/41 com 2 testes opcionais ignorados; self-test do inventário registrou as dez ferramentas;
- gateway/fake provider permaneceu verde: 30/30; release gate, syntax gate e Asset Modeler Phase 0 gate aprovados;
- nenhum smoke real ou operação faturável foi executado nesta fase automatizada.

### 16.4 Evidência das Fases 4–7

- `ai3d_plan_workflow` aceita quatro tipos fechados: `generate_asset`, `retopology`, `segment_complete` e `rig_animation`; a superfície MCP permanece com dez ferramentas e sem endpoint cru;
- cada plano persiste a lista de nós, payload tipado sem input sensível, estimate/cap por nó, índice atual e jobs remotos; a retomada usa as mesmas idempotency keys e não repete upload após falha de criação;
- Smart Retopology v2 aplica os limites oficiais de 500–20.000 triângulos ou 500–10.000 quads; v1 aplica seus limites separados e recusa parâmetros exclusivos da v2;
- a qualidade A/B mede triângulos, redução, bounds e canais; retopologia fora do budget é bloqueada e a aceitação exige o hash do preview lado a lado atual;
- segmentação v2 suporta `simple|balanced|detailed`, connectivity split e completion; fontes fora de `Assets/` não podem provar preservação de rig e são bloqueadas, enquanto perdas conhecidas apontam para `mesh_preview_smart_split`/`mesh_apply_smart_split`/`mesh_validate_split`;
- rig executa `rig_check` antes do nó faturável, usa o tipo recomendado quando `rigType=auto`, aceita Mixamo/Tripo, retarget batch em FBX e valida esqueleto, pesos, bind poses e clips com o Rig & Animation Studio;
- AI 3D Studio ganhou página Processar com Retopologia, Separar + fechar e Rig + animação, usando o mesmo planner/runner MCP e uma única confirmação revisável;
- webhook valida assinatura e timestamp, compara o header de evento, deduplica delivery, reabre a entrega se a reconciliação falhar e impede que o polling concorrente baixe o artifact duas vezes;
- quotas não quebram replay idempotente; telemetria resume somente contagens/operações/créditos e declara explicitamente não conter prompts ou credenciais;
- CI `.github/workflows/ai3d-contracts.yml` executa gateway tests e gates estáticos sem segredo/Tripo real;
- candidatos barrados pelo gate continuam inspecionáveis na quarentena: a janela mostra métricas, issues e ferramentas locais sugeridas, permite preview/rejeição e mantém a aceitação desabilitada;
- Unity 6000.3.5f2, projeto isolado: AI3D EditMode aprovou 21/21; contrato MCP aprovou 41/41, com 2 testes opcionais ignorados; gateway/fake provider aprovou 33/33; release, syntax e Asset Modeler Phase 0 gates aprovados;
- nenhum crédito foi consumido e o projeto Unity ativo permaneceu sem mutação.

## 17. Referências oficiais

- <https://developers.tripo3d.ai/en/docs/authentication>
- <https://developers.tripo3d.ai/en/docs/quick-start>
- <https://developers.tripo3d.ai/en/docs/task-query>
- <https://developers.tripo3d.ai/en/docs/files>
- <https://developers.tripo3d.ai/en/docs/rate-limits>
- <https://developers.tripo3d.ai/en/docs/webhooks>
- <https://developers.tripo3d.ai/en/docs/mesh-decimate>
- <https://developers.tripo3d.ai/en/docs/mesh-segment>
- <https://developers.tripo3d.ai/en/docs/mesh-complete>
- <https://developers.tripo3d.ai/en/docs/models-convert>
- <https://developers.tripo3d.ai/en/docs/animations-rig>
- <https://developers.tripo3d.ai/en/docs/animations-retarget>
