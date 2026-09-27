# Live2D runtime files

This directory contains the runtime assembled from the user-provided Cubism SDK for Web 5-r.5:

- `live2dcubismcore.js`: the licensed Cubism Core build.
- `Shaders/`: official WebGL shader sources.
- `modules/`: the DesktopPet adapter and official Cubism Web Framework compiled as ES Modules.

The adapter exposes `window.desktopPetCubism.create(canvas, config)`. It returns an object with `ready` (a promise), `setState(state)`, `setPointer(x, y)`, and `dispose()` members. Pointer coordinates are clamped to `[-1, 1]` and forwarded to Cubism's dragging target. `config.pointerTracking` supplies the parameter ID, impact percentage, reflection flag, and X/Y source for every enabled parameter; the runtime derives its coefficient from the parameter range embedded in the MOC3. `config` also contains `modelUrl`, `scale`, `offsetX`, `offsetY`, and an Idle/Click/Dragging motion-group map.

Rebuild the modules from `src/DesktopPet.Live2D.Web` with `pnpm install` and `pnpm build`. The build also adds explicit `.js` suffixes required by Chromium's ES module loader.

The official files retain their Live2D licenses. Do not redistribute Cubism Core or publish an expandable application until the applicable Live2D SDK agreements have been reviewed.
