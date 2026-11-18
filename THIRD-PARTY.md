# Componentes de terceiros

| Componente | Uso | Licença |
|---|---|---|
| [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (Direct3D11, DXGI, D3DCompiler) | modo shader e modo escala | MIT |
| [SharpGen.Runtime](https://github.com/SharpGenTools/SharpGen) | dependência do Vortice | MIT |
| Material Symbols Rounded (Google) | ícones da interface | Apache 2.0 |
| [AMD FidelityFX FSR 1](https://github.com/GPUOpen-Effects/FidelityFX-FSR) (`ffx_a.h`, `ffx_fsr1.h`, sem alterações) | modo escala: EASU e RCAS | MIT, ver `ScreenMaster/Vendor/fsr/LICENSE.txt` |
| [NVIDIA Image Scaling](https://github.com/NVIDIAGameWorks/NVIDIAImageScaling) (`NIS_Scaler.h`, `NIS_Config.h`, sem alterações; constantes portadas para C#) | modo escala: NIS | MIT, ver `ScreenMaster/Vendor/nis/LICENSE.txt` |
| [Super xBR](https://github.com/libretro/slang-shaders) (Hyllian), traduzido de GLSL para HLSL | modo escala: Super xBR | MIT, ver `ScreenMaster/Vendor/xbr/LICENSE.txt` |
| [Anime4K](https://github.com/bloc97/Anime4K) (bloc97), Upscale CNN x2 S e M traduzidos de GLSL para HLSL | modo escala: Anime4K | MIT, ver `ScreenMaster/Vendor/anime4k/LICENSE.txt` |
| [Snapdragon Game Super Resolution 1](https://github.com/SnapdragonStudios/snapdragon-gsr) (`sgsr1_mobile.h`, sem alterações) | modo escala: SGSR | BSD-3-Clause, ver `ScreenMaster/Vendor/sgsr/LICENSE.txt` |

Algoritmos reimplementados a partir de descrições publicadas (sem copiar código): nitidez adaptativa no estilo do AMD FidelityFX CAS (MIT), simulação de daltonismo de Machado, Oliveira & Fernandes (2009), temperatura de cor pelo ajuste de Tanner Helland.

Os arquivos de terceiros ficam em `ScreenMaster/Vendor/`, com a licença original de cada um, e vão embutidos no executável.

> Copyright (c) 2021 Advanced Micro Devices, Inc. — FidelityFX FSR 1, licença MIT.
> Copyright (c) 2022 NVIDIA CORPORATION & AFFILIATES — NVIDIA Image Scaling, licença MIT.
> Copyright (c) 2015 Hyllian — Super xBR, licença MIT.
> Copyright (c) 2019 bloc97 — Anime4K, licença MIT.
> Copyright (c) 2023, Qualcomm Innovation Center, Inc. — Snapdragon GSR, licença BSD-3-Clause. Este produto não é endossado pela Qualcomm.
