# Meshy AI no AI 3D Studio — especificação e acompanhamento

> Estado: implementado em código em 2026-08-10; smoke pago e aprovação visual real ainda pendentes.

## 1. Objetivo

Adicionar Meshy como segundo provedor do pipeline AI 3D sem criar um caminho de segurança paralelo. Meshy produz candidatos; o gateway continua sendo a única fronteira de credencial/rede e a Unity continua autoritativa para importar, validar, revisar, aceitar e adicionar assets à cena.

## 2. Contrato funcional

| Workflow do produto | Tripo | Meshy | `auto` |
|---|---|---|---|
| Texto → 3D | sim | preview + refine opcional | Meshy |
| Imagem → 3D | sim | standard ou Smart Topology T2 | Meshy |
| Retopologia | Smart Retopology | Remesh tri/quad | Meshy |
| Separar + fechar | sim | não disponível | Tripo |
| Rig + animação | rig-check, rig e retarget | humanoide bípede, rig e action IDs | Meshy |

Operações Meshy aceitas no contrato do gateway: `text_to_model`, `image_to_model`, `multiview_to_model`, `texture`, `convert`, `retopology`, `resize`, `uv_unwrap`, `rig` e `animate`. Nenhum endpoint arbitrário é exposto ao MCP ou à Unity.

## 3. Arquitetura

```text
AI 3D Studio / ai3d_* MCP
        |
        | plano local, hash, orçamento, consentimento
        v
Gateway loopback autenticado
  +-- TripoHttpProvider
  +-- MeshyHttpProvider
        |
        +-- jobs duráveis provider-scoped
        +-- polling tipado por operação
        +-- artifact vault com SHA-256
        v
Quarentena Unity -> preview -> validação -> aceitação explícita
```

O `provider` faz parte do plano canônico, request hash, registro durável, telemetria, upload e resposta pública. Registros legados sem o campo continuam sendo lidos como `tripo`.

## 4. Credenciais e rede

- Tripo exige chave `tsk_`; Meshy exige chave `msy_`.
- Cada chave ocupa uma entrada própria no Windows Credential Manager.
- A Unity mantém a chave somente no campo transitório até o PUT autenticado e limpa o valor ao terminar.
- Fallbacks de ambiente: `TRIPO_API_KEY` e `MESHY_API_KEY`.
- Bases oficiais padrão: `https://openapi.tripo3d.ai/v3` e `https://api.meshy.ai`.
- Hosts customizados continuam bloqueados salvo opt-in de desenvolvimento já existente.
- Downloads aceitam somente HTTPS de domínios oficiais Tripo ou Meshy; a validação Unity cruza o host com o provedor do job para impedir troca entre provedores.

## 5. Upload Meshy

Meshy aceita URL pública ou Data URI. O gateway não persiste base64 nem conteúdo bruto no job. Ele:

1. valida raiz, extensão, tamanho e SHA-256 do arquivo aprovado;
2. copia para staging local nomeado pelo hash;
3. retorna um handle opaco `meshy-local:<sha256>:<ext>`;
4. confirma novamente o hash e materializa a Data URI somente no adaptador, imediatamente antes da chamada aprovada.

O handle, não o conteúdo, entra no registro durável.

## 6. Mapeamento Meshy

- Texto usa `/openapi/v2/text-to-3d`, `mode=preview`; textura cria um segundo nó `mode=refine` ligado ao preview.
- Imagem usa `/openapi/v1/image-to-3d`; Smart Low Poly mapeia para `model_type=smart-topology`, `ai_model=meshy-t2`, com `target_polycount` entre 100 e 15.000.
- Remesh usa `/openapi/v1/remesh`, triângulos ou quad-dominant, de 100 a 300.000 polígonos.
- Convert solicita formatos explicitamente e custa um nó separado.
- Rig usa `/openapi/v1/rigging`, altura positiva e entrada GLB/task. Para modelo externo: humanoide texturizado, frente +Z e até 300.000 faces.
- Animação usa `/openapi/v1/animations`, action ID positivo e FPS estritamente em 24, 25, 30 ou 60.
- Resize usa `resize_height` e origem `bottom|center`.
- UV unwrap permanece disponível no gateway para evolução do planner; exige GLB e até 40.000 faces segundo o contrato do provedor.

## 7. Orçamento e confirmação

O planner usa estimativas por nó e nunca confunde o teto local com garantia transacional do provedor. A precificação Meshy observada na documentação durante esta implementação é registrada como: convert 1, resize 1, remesh 5, rig 5 e animação 3 créditos por ação; geração/refine varia pelo modo. Mudanças futuras de preço exigem atualizar catálogo/testes antes de alterar defaults.

Planejar nunca chama o provedor. Iniciar pelo MCP continua exigindo:

- política de serviço externo habilitada;
- `planHash` atual;
- aprovação do teto exato;
- consentimento dos tipos de dados enviados;
- confirmação MCP de uso único.

## 8. Editor Unity

- seletor Tripo, Meshy ou Automático nas páginas Gerar e Processar;
- estado e credencial separados por provedor na Configuração;
- saldo consultado para o provedor efetivo;
- opções Meshy específicas para Smart Topology, altura do humanoide, action IDs e FPS;
- jobs mostram o provedor;
- `Separar + fechar` explica e bloqueia Meshy;
- resultados continuam fora da cena até quarentena, preview e aceitação.

## 9. MCP

`ai3d_plan_workflow` aceita `provider=tripo|meshy|auto`, `heightMeters`, `animationActionIds` e `animationFps`. `ai3d_list_providers` retorna credencial segura, operações e workflows suportados por provedor. Não existe ferramenta para fornecer chaves ou chamar uma URL bruta.

## 10. Critérios e evidência

- [x] Gateway multi-provider e armazenamento de credencial separado.
- [x] Adaptador Meshy e normalização do ciclo assíncrono.
- [x] Upload local hash-bound sem persistir Data URI no job.
- [x] Vault recursivo para modelos, rig e animações Meshy.
- [x] Planner/runner, UI e MCP provider-aware.
- [x] Validação Unity de host vinculada ao provedor.
- [x] Regressão Python: 39 testes aprovados.
- [x] Regressão Unity AI3D EditMode: 23 testes aprovados.
- [ ] Teste de saldo com chave real do usuário.
- [ ] Um smoke pago explicitamente aprovado por workflow escolhido.
- [ ] Prova visual real de importação, materiais, escala, rig e clip na Unity ativa.

Os três itens finais não podem ser automatizados sem autorização para consumo e inspeção visual do resultado real.
