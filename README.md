# ScreenMaster

Camadas de ajuste estilo Photoshop para a área de trabalho inteira do Windows: presets, agenda por horário, regras por programa e efeitos que valem para tudo o que está na tela.

**Baixar:** [`dist/ScreenMaster.exe`](dist/ScreenMaster.exe) (Windows 10 2004+ / 11, x64, não precisa instalar).

## O que faz
- **Cor e tons:** Matiz/Saturação, Equilíbrio de cores, Preto e branco, Filtro de fotos, Misturador de canais, Simular daltonismo, Brilho/Contraste, Níveis, Curvas, Exposição, Temperatura, Lift/Gamma/Gain, Mapa de degradê, Posterizar, Limiar
- **Sobreposição:** Granulação, Cor sólida, Degradê, Vinheta, Guias e grade, Padrão
- **Monitor (DDC/CI):** brilho, contraste e cor dentro do próprio monitor
- **Sistema:** tema claro/escuro do Windows
- **Shader (experimental):** Pesquisa de cores (LUT 3D `.cube`), Cor seletiva, Nitidez / Clareza / Remover névoa, com custo de GPU medido por camada
- **Modo escala (camada Shader, experimental):** amplia a janela de um jogo para a tela inteira (Bilinear, Bicúbico, Lanczos 3, AMD FSR 1, Snapdragon GSR, NVIDIA NIS, Super xBR e Anime4K CNN, com nitidez RCAS do FSR 1), com proporção, escala inteira e cursor mapeado
- **Agenda:** automações por horário e por dia da semana; **regras por programa** trocam preset (inclusive um com Modo escala) ou pausam a agenda

## Atalhos
| Atalho | Ação |
|---|---|
| Ctrl+Alt+Shift+S | liga/desliga os ajustes |
| Ctrl+Alt+Shift+P | próximo preset |
| Ctrl+Alt+Shift+B | antes/depois |
| Ctrl+Alt+Shift+E | modo escala: pausa/retoma, ou liga na janela da frente |
| Ctrl+Alt+Shift+R | restaurar a tela (emergência) |

## Compilar
.NET 10 SDK, Windows x64:

```
cd ScreenMaster
dotnet publish -c Release -o ../dist
```

Checagem sem mexer na tela: `ScreenMaster.exe --shadercheck` (resultado em `%APPDATA%\ScreenMaster\shadercheck.txt`). Restaurar a tela depois de um travamento: `ScreenMaster.exe --restore`.

## Planos
Ver [PLANO-SHADER.md](PLANO-SHADER.md). Bibliotecas de terceiros em [THIRD-PARTY.md](THIRD-PARTY.md).
