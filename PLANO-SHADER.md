# ScreenMaster — plano do modo shader e "tipo DLSS"

> **Retirado em 19/11/2026.** O modo shader (captura da tela + redesenho) e o modo escala foram removidos do app por não funcionarem bem na prática, junto com os atalhos globais. O código está no histórico do git (até o commit `7445541`).

Tudo experimental, desligado por padrão, fora dos presets padrão. Funciona só em janela/sem bordas e em monitor SDR.
Antes de embutir qualquer código de terceiros: confirmar licença e anotar em `THIRD-PARTY.md`.

## Já pronto (build atual)
- Padrão, Mapa de degradê (sem shader)
- Motor shader: captura da tela, janela fora da captura, watchdog, esconde em UAC/tela cheia/HDR/DRM
- Pesquisa de cores (.cube), Cor seletiva, Nitidez / Clareza / Remover névoa

## Fase 0 — validar o que existe (você testa)
- Roteiro: aparece e deixa clicar · não se autocaptura (Clareza +100) · matriz de cor não duplica · some no UAC e em tela cheia
- Corrigir o que aparecer antes de seguir

## Fase 1 — base para muitos efeitos
- Cadeia de passes: cada camada Shader vira um passe, na ordem da pilha (hoje são 3 slots fixos)
- Texturas intermediárias, passes com resolução diferente, shaders pré-compilados no build
- Medidor de custo por camada (ms de GPU) no inspector

## Fase 2 — Modo escala (o "tipo DLSS")
- Atalho Ctrl+Alt+Shift+E: escala a janela em foco para a tela inteira
- Captura só da janela (Windows.Graphics.Capture), saída em tela cheia por cima
- Mouse: converter posição/cliques da tela ampliada para a janela real (como o Magpie)
- Proporção / letterbox (manter, 4:3, 16:9, 21:9) e escala inteira (2×, 3×…)
- Regra por programa: escalar sozinho quando o jogo abrir

## Fase 3 — Upscalers (escolha no modo escala)
| Upscaler | Uso | Licença (confirmar) |
|---|---|---|
| Bilinear / Bicubic / Lanczos | referência | livre |
| FSR 1 (EASU) + RCAS | padrão recomendado | MIT |
| NVIDIA NIS | alternativa | MIT |
| SGSR | GPU fraca | BSD-3 |
| Anime4K | anime / 2D | MIT |
| xBR / ScaleFX | pixel art | MIT / ver |
| Escala inteira | pixel art sem borrão | — |
| FSRCNNX / RAVU | neural leve | LGPL, ver |

## Fase 4 — Efeitos de imagem (novas camadas Shader)
- Anti-aliasing: SMAA, FXAA
- Deband, Denoise leve
- Bloom, Aberração cromática / lente
- CRT: CRT-Geom (e CRT-Royale se a licença permitir distribuir)

## Fase 5 — Nitidez de movimento (BenQ 144 Hz)
- Inserção de quadro preto (BFI)
- Simulador de feixe CRT (Blur Busters, MIT)
- Aviso de cintilação/fotossensibilidade antes de ligar

## Fase 6 — Geração de frames (experimental)
- RIFE via ONNX + DirectML (qualquer GPU), 2× FPS
- Avisos: atraso maior, artefatos no HUD

## Fase 7 — IA de imagem
- Look automático: Zero-DCE++ → camada de Curvas automática (sem atraso)
- Preset por cena: MobileCLIP troca o preset sozinho
- LUT por cena: Image-Adaptive-3DLUT sobre a Pesquisa de cores
- Upscaler neural (ESRGAN pequeno) no modo escala

## Fase 8 — Overlay no jogo
- HUD por atalho: preset atual, trocar preset, 2–3 sliders rápidos
- Aviso rápido ao trocar de preset
- Contador de FPS (PresentMon/ETW), opcional

## Não dá (precisa de dados de dentro do jogo)
FSR 2/3/4, XeSS, DLSS, frame gen do FSR 3, SSAO, profundidade de campo, iluminação do ReShade.
No driver (só orientar): AFMF, RTX Video Super Resolution, Auto SR.
