/**
 * DesktopPet adapter around Live2D Cubism SDK for Web 5-r.5.
 * Framework/sample portions retain their upstream Live2D Open Software License.
 */
import { CubismFramework, LogLevel, Option } from '@framework/live2dcubismframework.js';
import { LAppPal } from './lapppal.js';
import { LAppSubdelegate } from './lappsubdelegate.js';
import { configureDesktopPetModel } from './lapplive2dmanager.js';
import { configureIdleMotion } from './lappdefine.js';
class DesktopPetCubismRenderer {
    constructor(canvas, config) {
        this.frameRequest = 0;
        this.disposed = false;
        this.update = () => {
            if (this.disposed)
                return;
            LAppPal.updateTime();
            this.subdelegate.update();
            this.frameRequest = requestAnimationFrame(this.update);
        };
        configureIdleMotion(config.motions.idle ?? '');
        configureDesktopPetModel(config);
        const option = new Option();
        option.logFunction = message => console.debug(`[Cubism] ${message}`);
        option.loggingLevel = LogLevel.LogLevel_Warning;
        if (!CubismFramework.startUp(option))
            throw new Error('Cubism Framework 启动失败。');
        CubismFramework.initialize();
        LAppPal.updateTime();
        this.subdelegate = new LAppSubdelegate();
        if (!this.subdelegate.initialize(canvas))
            throw new Error('WebGL2 初始化失败。');
        this.ready = this.waitUntilReady();
        this.frameRequest = requestAnimationFrame(this.update);
    }
    setState(state) {
        if (this.disposed)
            return;
        this.subdelegate.getLive2DManager().setState(state);
    }
    setPointer(x, y) {
        if (this.disposed)
            return;
        this.subdelegate.getLive2DManager().onDrag(Math.max(-1, Math.min(1, x)), Math.max(-1, Math.min(1, y)));
    }
    dispose() {
        if (this.disposed)
            return;
        this.disposed = true;
        cancelAnimationFrame(this.frameRequest);
        this.subdelegate.release();
        CubismFramework.dispose();
    }
    async waitUntilReady() {
        const deadline = performance.now() + 30000;
        while (!this.disposed && performance.now() < deadline) {
            if (this.subdelegate.getLive2DManager().isReady())
                return;
            await new Promise(resolve => setTimeout(resolve, 50));
        }
        throw new Error('Live2D 模型在 30 秒内未完成加载。');
    }
}
window.desktopPetCubism = {
    create: (canvas, config) => new DesktopPetCubismRenderer(canvas, config)
};
