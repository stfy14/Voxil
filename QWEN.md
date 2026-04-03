# Voxil — Voxel Game Engine (GPU Raycasting)

## Project Overview

Voxil — воксельный игровой движок на C# (.NET 8) с рендерингом через GPU raycasting. Реализует 3D-мир из вокселей с физикой в реальном времени, динамическим циклом дня/ночи, глобальным освещением через Voxel Cone Tracing (VCT) Clipmap и встроенным редактором моделей. Использует OpenTK для OpenGL-рендеринга и BepuPhysics для физики.

### Ключевые особенности
- **GPU Raycasting Renderer** — рендеринг вокселей через compute-шейдеры и ray marching
- **Динамический цикл дня/ночи** — настраиваемая система времени с атмосферным рендерингом
- **Глобальное освещение (GI)** — Anisotropic Voxel Cone Tracing с 3-уровневым клипмапом
- **Физика** — интеграция BepuPhysics с разрушаемостью вокселей
- **Асинхронная генерация чанков** — многопоточная генерация мира с планировщиком
- **Редактор** — встроенный редактор воксельных моделей с ImGui UI
- **SVO (Sparse Voxel Octree)** — GPU-пул для динамических объектов

### Технологии и зависимости
- **.NET 8** — целевой фреймворк
- **OpenTK 4.9.4** — OpenGL windowing и графика (OpenGL 4.5 Core profile)
- **BepuPhysics 2.5.0-beta.27** — физический движок
- **ImGui.NET 1.91.6.1** — immediate mode GUI
- **StbImageSharp / StbTrueTypeSharp** — загрузка изображений и шрифтов
- **GLSL шейдеры** — compute, vertex, fragment шейдеры для пайплайна рендеринга

## Render Pipeline

Полный конвейер рендеринга за кадр (метод `Render(CameraData cam)`):

### 1. Raycasting Pass (основной)
- **Шейдер:** `raycast.vert` + `raycast.frag`
- **Вход:** Page Table 3D текстура (`R32UI`), воксельные SSBO-банки (до 8 банков по 256 MB), mask SSBO, object grid
- **Логика:** Для каждого пикселя выпускается луч из камеры. Луч шагает по воксельной сетке мира через Page Table → находит пересечение с вокселями → вычисляет освещение (диффузное, specular, AO, тени от солнца/луны)
- **FBO:** `_mainFbo` → `_gColorTexture` (Rgba16f) + `_gDataTexture` (Rgba16f) + depth (DepthComponent32f)

### 2. Beam Optimization Pass (опционально)
- **Шейдер:** тот же raycast shader с флагом `uIsBeamPass = 1`
- **Цель:** Предварительный проход при уменьшенном разрешении (÷4) для определения дальних пустых лучей
- **FBO:** `_beamFbo` → `_beamTexture` (R32f, разрешение _renderWidth/4 × _renderHeight/4)

### 3. VCT Clipmap Update (если GI включён, после 15 кадров)
- **Шейдер:** `vct_clipmap_build.comp` (compute shader)
- **Цель:** Построение 3-уровневого анизотропного клипмапа глобального освещения
  - **L0:** CELL=2m, 64³ вокселей
  - **L1:** CELL=8m, 64³ вокселей
  - **L2:** CELL=32m, 64³ вокселей
- **Текстуры:** 6 × 3D текстуры (radiance Rgba16f + anisotropic opacity Rgba16f для каждого уровня)
- **Алгоритм:** Каждый кадр обновляется `SLICES_PER_FRAME` (4) Z-слайсов каждого уровня. Полный обход = 16 кадров (~0.27с при 60fps)
- **Освещение:** Запускает ray march-лучи из каждой ячейки клипмапа, аккумулирует radiance с учётом sun/point lights

### 4. Shadow Pass
- **Шейдер:** `raycast.vert` + `shadow.frag` (define `SHADOW_PASS`)
- **Цель:** Тени от солнца/луны + point light shadows
- **FBO:** `_shadowFbo` → `_shadowTexture` (Rg16f) + `_shadowPointLightTexture` (Rgba16f)
- **Разрешение:** downscaled в `GameSettings.ShadowDownscale` (по умолчанию ÷1 = full res)

### 5. Shadow Upsample Pass
- **Шейдер:** `raycast.vert` + `shadow_upsample.frag`
- **Цель:** Апсемплинг теней до полного разрешения
- **FBO:** `_shadowFullFbo` → `_shadowFullTexture` (Rg16f) + `_shadowPointLightFullTexture` (Rgba16f)

### 6. Composite Pass
- **Шейдер:** `raycast.vert` + `composite.frag`
- **Вход:** `_gColorTexture`, `_gDataTexture`, `_shadowFullTexture`, `_shadowPointLightFullTexture`
- **Логика:** Финальная композиция цвета с тенями, атмосферой, постобработкой
- **FBO:** `_compositeFbo` → `_mainColorTexture` (Rgba16f)

### 7. TAA Pass (если включён)
- **Шейдер:** `raycast.vert` + `taa.frag`
- **Цель:** Temporal Anti-Aliasing с Halton-последовательностью jitter и 2-кадровой историей
- **FBO:** ping-pong `_historyFbo[0/1]` → `_historyTexture[0/1]` (Rgba16f)

### 8. Debug Pass (опционально)
- **Цель:** Отрисовка дебаг-линий (коллизии, взрывы и т.д.) поверх финальной картинки с корректным Z-buffer
- **FBO:** `_debugFbo` → `_debugColorTexture` (Rgba16f) + shared depth from `_mainDepthTexture`

### 9. Present
- Blit из финального FBO (history или composite) на экран через `_debugFbo`

### Compute-шейдеры (вне Render, вызываются отдельно)
- **`grid_update.comp`** — построение object grid (linked-list voxel presence) для динамических объектов
- **`clear_grid.comp`** — очистка object grid перед перестройкой
- **`edit_updater.comp`** — применение точечных редактирований вокселей на GPU

## Project Structure

```
Voxil/
├── Core/              # Ядро: Game, Program, ServiceLocator, EventBus, Settings
├── Engine/            # Подсистемы движка
│   ├── Graphics/      # Рендеринг
│   │   ├── Renderers/ # GpuRaycastingRenderer, SVOBuilder, DynSvoManager
│   │   ├── GI/        # VCTSystem — Anisotropic Voxel Cone Tracing
│   │   ├── Shader/    # Shader, ShaderSystem, ShaderDefines, ShaderPaths
│   │   ├── GLSL/      # GLSL шейдеры + include/
│   │   └── ...        # Camera, CameraData, Crosshair, OrbitalCamera
│   ├── World/         # WorldManager, Chunk, VoxelObject, сервисы
│   ├── Physics/       # PhysicsWorld, отладка физики
│   ├── Input/         # InputManager
│   ├── Scene/         # SceneManager, GameScene, EditorScene
│   ├── UI/            # WindowManager, ImGui-окна
│   └── Diagnostic/    # PerformanceMonitor
├── Game/              # Игровая логика
│   ├── Player/        # PlayerController
│   ├── Entities/      # EntityManager
│   ├── Items/         # Система предметов
│   ├── Systems/       # Игровые системы
│   ├── UI/            # Инвентарь и т.д.
│   └── WorldGen/      # Генерация мира
├── Editor/            # Инструменты редактора воксельных моделей
├── Scripting/         # (пусто — задел на скриптинг)
├── Tests/             # TestManager, VoxelSystemTester
├── Assets/            # Шрифты
└── Engine/Graphics/GLSL/  # Файлы шейдеров
    ├── raycast.vert / raycast.frag
    ├── shadow.frag / shadow_upsample.frag
    ├── composite.frag / taa.frag
    ├── grid_update.comp / clear_grid.comp / edit_updater.comp
    ├── vct_clipmap_build.comp
    └── include/ (common, lighting, tracing, atmosphere, water, postprocess, vct)
```

## Building and Running

### Требования
- .NET 8 SDK
- GPU с поддержкой OpenGL 4.5

### Сборка
```bash
dotnet build
```

### Запуск
```bash
dotnet run --project Voxil/Voxil.csproj
```

### Release-сборка
```bash
dotnet build --configuration Release
```

### Запуск тестов
```bash
dotnet test
```

## Architecture Notes

### Система чанков
- Чанки: 16³ вокселей (ChunkResolution=16), ChunkSizeWorld=16м
- Page Table: 512×16×512 (PT_X×PT_Y×PT_Z) — 3D текстура `R32UI`, маппит мировые координаты чанков на GPU-слоты
- VRAM-банки: до 8 штук по 256 MB каждый (итого до 2 GB)
- Маски чанков: отдельный SSBO для битовых масок присутствия
- Uniform-чанки: помечаются флагом `SOLID_CHUNK_FLAG` в Page Table, не требуют SSBO-слота

### SVO (Sparse Voxel Octree)
- **SVOBuilder** — строит BFS-сериализованный SVO из набора вокселей. Формат узла: `[childMask, childOffset, material, padding]` (uvec4, 16 байт)
- **DynSvoManager** — единый GPU-пул (2M узлов × 16 байт = 32 MB) для всех динамических объектов. Синхронный билд для ≤256 вокселей, async через `Task.Run` для больших

### VCT (Voxel Cone Tracing) Global Illumination
- **3 уровня клипмапа:** L0 (2м/ячейка), L1 (8м), L2 (32м) — каждый 64³
- **Анизотропная opacity:** отдельно по осям X/Y/Z для корректного затухания
- **Incremental update:** 4 Z-слайса за кадр, полный обход за 16 кадров
- **Multi-bounce:** каждый уровень читает radiance предыдущего (L1 читает L0, L2 читает L1)
- **Point lights:** до 32 источников света передаются в VCT-шейдер

### Рендеринг
- **Текстуры:** G-Buffer из 2 текстур (цвет + данные) + depth
- **Тени:** 2-проходные — downscale → upsample для сглаживания
- **TAA:** Halton jitter, 2-кадровая история, ping-pong FBO
- **LOD:** Настраиваемый, с отключением эффектов на дальних расстояниях

### Управление памятью
- GC: `SustainedLowLatency`
- `ArrayPool<T>` для повторного использования буферов чанков
- Reallocation: полная очистка и пересоздание всех GPU-ресурсов при нехватке VRAM

### Конфигурация

Ключевые настройки в `GameSettings.cs`:
- **RenderDistance:** 64 (чанки)
- **EnableTAA:** false (по умолчанию)
- **EnableGI:** true
- **ShadowDownscale:** 1 (full resolution)
- **SoftShadowSamples:** 8
- **BeamOptimization:** true
- **LOD:** включён, 85% от render distance

## Notable Implementation Details

- **OpenGL:** 4.5 Core profile
- **Platform:** x64 only
- **Unsafe code:** разрешён
- **Vsync:** отключён
- **Default resolution:** 1280×720
- **Разрешение рендера:** настраивается через `RenderScale` (по умолчанию 1.0x)
