# Componentes de terceiros

| Componente | Uso | Licença |
|---|---|---|
| [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (Direct3D11, DXGI, D3DCompiler) | modo shader e modo escala | MIT |
| [SharpGen.Runtime](https://github.com/SharpGenTools/SharpGen) | dependência do Vortice | MIT |
| Material Symbols Rounded (Google) | ícones da interface | Apache 2.0 |
| [AMD FidelityFX FSR 1](https://github.com/GPUOpen-Effects/FidelityFX-FSR) (`ffx_a.h`, `ffx_fsr1.h`, sem alterações) | modo escala: EASU e RCAS | MIT, ver `ScreenMaster/Vendor/fsr/LICENSE.txt` |
| [Snapdragon Game Super Resolution 1](https://github.com/SnapdragonStudios/snapdragon-gsr) (`sgsr1_mobile.h`, sem alterações) | modo escala: SGSR | BSD-3-Clause, ver `ScreenMaster/Vendor/sgsr/LICENSE.txt` |

Algoritmos reimplementados a partir de descrições publicadas (sem copiar código): nitidez adaptativa no estilo do AMD FidelityFX CAS (MIT), simulação de daltonismo de Machado, Oliveira & Fernandes (2009), temperatura de cor pelo ajuste de Tanner Helland.

Os arquivos de terceiros ficam em `ScreenMaster/Vendor/`, com a licença original de cada um, e vão embutidos no executável.

> Copyright (c) 2021 Advanced Micro Devices, Inc. — FidelityFX FSR 1, licença MIT.
> Copyright (c) 2023, Qualcomm Innovation Center, Inc. — Snapdragon GSR, licença BSD-3-Clause. Este produto não é endossado pela Qualcomm.
