/**
 * DesktopPet adapter around Live2D Cubism SDK for Web 5-r.5.
 * Framework/sample portions retain their upstream Live2D Open Software License.
 */
import { CubismFramework, LogLevel, Option } from '@framework/live2dcubismframework';
import { LAppPal } from './lapppal';
import { LAppSubdelegate } from './lappsubdelegate';
import { configureDesktopPetModel } from './lapplive2dmanager';
import { configureIdleMotion } from './lappdefine';

export interface DesktopPetConfig {
  modelUrl: string;
  scale: number;
  offsetX: number;
  offsetY: number;
  motions: Partial<Record<'idle' | 'click' | 'dragging', string>>;
  pointerTracking: Array<{
    parameterId: string;
    impact: number;
    reflect: boolean;
    source: 'mouseLeftX' | 'mouseLeftY';
  }>;
}

class DesktopPetCubismRenderer {
  public readonly ready: Promise<void>;
  private readonly subdelegate: LAppSubdelegate;
  private frameRequest = 0;
  private disposed = false;

  public constructor(canvas: HTMLCanvasElement, config: DesktopPetConfig) {
    configureIdleMotion(config.motions.idle ?? '');
    configureDesktopPetModel(config);

    const option = new Option();
    option.logFunction = message => console.debug(`[Cubism] ${message}`);
    option.loggingLevel = LogLevel.LogLevel_Warning;
    if (!CubismFramework.startUp(option)) throw new Error('Cubism Framework 启动失败。');
    CubismFramework.initialize();
    LAppPal.updateTime();

    this.subdelegate = new LAppSubdelegate();
    if (!this.subdelegate.initialize(canvas)) throw new Error('WebGL2 初始化失败。');
    this.ready = this.waitUntilReady();
    this.frameRequest = requestAnimationFrame(this.update);
  }

  public setState(state: string): void {
    if (this.disposed) return;
    this.subdelegate.getLive2DManager().setState(state);
  }

  public setPointer(x: number, y: number): void {
    if (this.disposed) return;
    this.subdelegate.getLive2DManager().onDrag(
      Math.max(-1, Math.min(1, x)),
      Math.max(-1, Math.min(1, y))
    );
  }

  public dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    cancelAnimationFrame(this.frameRequest);
    this.subdelegate.release();
    CubismFramework.dispose();
  }

  private readonly update = (): void => {
    if (this.disposed) return;
    LAppPal.updateTime();
    this.subdelegate.update();
    this.frameRequest = requestAnimationFrame(this.update);
  };

  private async waitUntilReady(): Promise<void> {
    const deadline = performance.now() + 30000;
    while (!this.disposed && performance.now() < deadline) {
      if (this.subdelegate.getLive2DManager().isReady()) return;
      await new Promise(resolve => setTimeout(resolve, 50));
    }
    throw new Error('Live2D 模型在 30 秒内未完成加载。');
  }
}

declare global {
  interface Window {
    desktopPetCubism: {
      create(canvas: HTMLCanvasElement, config: DesktopPetConfig): DesktopPetCubismRenderer;
    };
  }
}

window.desktopPetCubism = {
  create: (canvas, config) => new DesktopPetCubismRenderer(canvas, config)
};
