/**
 * Copyright(c) Live2D Inc. All rights reserved.
 *
 * Use of this source code is governed by the Live2D Open Software license
 * that can be found at https://www.live2d.com/eula/live2d-open-software-license-agreement_en.html.
 */
import { CubismDefaultParameterId } from '@framework/cubismdefaultparameterid.js';
import { CubismModelSettingJson } from '@framework/cubismmodelsettingjson.js';
import { BreathParameterData, CubismBreath } from '@framework/effect/cubismbreath.js';
import { LookParameterData, CubismLook } from '@framework/effect/cubismlook.js';
import { CubismEyeBlink } from '@framework/effect/cubismeyeblink.js';
import { CubismFramework } from '@framework/live2dcubismframework.js';
import { CubismUserModel } from '@framework/model/cubismusermodel.js';
import { ACubismMotion } from '@framework/motion/acubismmotion.js';
import { InvalidMotionQueueEntryHandleValue } from '@framework/motion/cubismmotionqueuemanager.js';
import { CubismUpdateScheduler } from '@framework/motion/cubismupdatescheduler.js';
import { CubismBreathUpdater } from '@framework/motion/cubismbreathupdater.js';
import { CubismLookUpdater } from '@framework/motion/cubismlookupdater.js';
import { CubismEyeBlinkUpdater } from '@framework/motion/cubismeyeblinkupdater.js';
import { CubismExpressionUpdater } from '@framework/motion/cubismexpressionupdater.js';
import { CubismPhysicsUpdater } from '@framework/motion/cubismphysicsupdater.js';
import { CubismPoseUpdater } from '@framework/motion/cubismposeupdater.js';
import { CubismLipSyncUpdater } from '@framework/motion/cubismlipsyncupdater.js';
import { CSM_ASSERT, CubismLogError, CubismLogInfo } from '@framework/utils/cubismdebug.js';
import * as LAppDefine from './lappdefine.js';
import { LAppPal } from './lapppal.js';
import { LAppWavFileHandler } from './lappwavfilehandler.js';
import { CubismMoc } from '@framework/model/cubismmoc.js';
var LoadStep;
(function (LoadStep) {
    LoadStep[LoadStep["LoadAssets"] = 0] = "LoadAssets";
    LoadStep[LoadStep["LoadModel"] = 1] = "LoadModel";
    LoadStep[LoadStep["WaitLoadModel"] = 2] = "WaitLoadModel";
    LoadStep[LoadStep["LoadExpression"] = 3] = "LoadExpression";
    LoadStep[LoadStep["WaitLoadExpression"] = 4] = "WaitLoadExpression";
    LoadStep[LoadStep["LoadPhysics"] = 5] = "LoadPhysics";
    LoadStep[LoadStep["WaitLoadPhysics"] = 6] = "WaitLoadPhysics";
    LoadStep[LoadStep["LoadPose"] = 7] = "LoadPose";
    LoadStep[LoadStep["WaitLoadPose"] = 8] = "WaitLoadPose";
    LoadStep[LoadStep["SetupEyeBlink"] = 9] = "SetupEyeBlink";
    LoadStep[LoadStep["SetupBreath"] = 10] = "SetupBreath";
    LoadStep[LoadStep["LoadUserData"] = 11] = "LoadUserData";
    LoadStep[LoadStep["WaitLoadUserData"] = 12] = "WaitLoadUserData";
    LoadStep[LoadStep["SetupEyeBlinkIds"] = 13] = "SetupEyeBlinkIds";
    LoadStep[LoadStep["SetupLipSyncIds"] = 14] = "SetupLipSyncIds";
    LoadStep[LoadStep["SetupLook"] = 15] = "SetupLook";
    LoadStep[LoadStep["SetupLayout"] = 16] = "SetupLayout";
    LoadStep[LoadStep["LoadMotion"] = 17] = "LoadMotion";
    LoadStep[LoadStep["WaitLoadMotion"] = 18] = "WaitLoadMotion";
    LoadStep[LoadStep["CompleteInitialize"] = 19] = "CompleteInitialize";
    LoadStep[LoadStep["CompleteSetupModel"] = 20] = "CompleteSetupModel";
    LoadStep[LoadStep["LoadTexture"] = 21] = "LoadTexture";
    LoadStep[LoadStep["WaitLoadTexture"] = 22] = "WaitLoadTexture";
    LoadStep[LoadStep["CompleteSetup"] = 23] = "CompleteSetup";
})(LoadStep || (LoadStep = {}));
/**
 * ユーザーが実際に使用するモデルの実装クラス<br>
 * モデル生成、機能コンポーネント生成、更新処理とレンダリングの呼び出しを行う。
 */
export class LAppModel extends CubismUserModel {
    /**
     * model3.jsonが置かれたディレクトリとファイルパスからモデルを生成する
     * @param dir
     * @param fileName
     */
    loadAssets(dir, fileName) {
        this._modelHomeDir = dir;
        fetch(`${this._modelHomeDir}${fileName}`)
            .then(response => response.arrayBuffer())
            .then(arrayBuffer => {
            const setting = new CubismModelSettingJson(arrayBuffer, arrayBuffer.byteLength);
            // ステートを更新
            this._state = LoadStep.LoadModel;
            // 結果を保存
            this.setupModel(setting);
        })
            .catch(error => {
            // model3.json読み込みでエラーが発生した時点で描画は不可能なので、setupせずエラーをcatchして何もしない
            CubismLogError(`Failed to load file ${this._modelHomeDir}${fileName}`);
        });
    }
    /**
     * model3.jsonからモデルを生成する。
     * model3.jsonの記述に従ってモデル生成、モーション、物理演算などのコンポーネント生成を行う。
     *
     * @param setting ICubismModelSettingのインスタンス
     */
    setupModel(setting) {
        this._updating = true;
        this._initialized = false;
        this._modelSetting = setting;
        // CubismModel
        if (this._modelSetting.getModelFileName() != '') {
            const modelFileName = this._modelSetting.getModelFileName();
            fetch(`${this._modelHomeDir}${modelFileName}`)
                .then(response => {
                if (response.ok) {
                    return response.arrayBuffer();
                }
                else if (response.status >= 400) {
                    CubismLogError(`Failed to load file ${this._modelHomeDir}${modelFileName}`);
                    return new ArrayBuffer(0);
                }
            })
                .then(arrayBuffer => {
                this.loadModel(arrayBuffer, this._mocConsistency);
                this._state = LoadStep.LoadExpression;
                // callback
                loadCubismExpression();
            });
            this._state = LoadStep.WaitLoadModel;
        }
        else {
            LAppPal.printMessage('Model data does not exist.');
        }
        // Expression
        const loadCubismExpression = () => {
            if (this._modelSetting.getExpressionCount() > 0) {
                const count = this._modelSetting.getExpressionCount();
                for (let i = 0; i < count; i++) {
                    const expressionName = this._modelSetting.getExpressionName(i);
                    const expressionFileName = this._modelSetting.getExpressionFileName(i);
                    fetch(`${this._modelHomeDir}${expressionFileName}`)
                        .then(response => {
                        if (response.ok) {
                            return response.arrayBuffer();
                        }
                        else if (response.status >= 400) {
                            CubismLogError(`Failed to load file ${this._modelHomeDir}${expressionFileName}`);
                            // ファイルが存在しなくてもresponseはnullを返却しないため、空のArrayBufferで対応する
                            return new ArrayBuffer(0);
                        }
                    })
                        .then(arrayBuffer => {
                        const motion = this.loadExpression(arrayBuffer, arrayBuffer.byteLength, expressionName);
                        if (this._expressions.get(expressionName) != null) {
                            ACubismMotion.delete(this._expressions.get(expressionName));
                            this._expressions.set(expressionName, null);
                        }
                        this._expressions.set(expressionName, motion);
                        this._expressionCount++;
                        if (this._expressionCount >= count) {
                            // Expression Updaterの追加
                            if (this._expressionManager != null) {
                                const expressionUpdater = new CubismExpressionUpdater(this._expressionManager);
                                this._updateScheduler.addUpdatableList(expressionUpdater);
                            }
                            this._state = LoadStep.LoadPhysics;
                            // callback
                            loadCubismPhysics();
                        }
                    });
                }
                this._state = LoadStep.WaitLoadExpression;
            }
            else {
                this._state = LoadStep.LoadPhysics;
                // callback
                loadCubismPhysics();
            }
        };
        // Physics
        const loadCubismPhysics = () => {
            if (this._modelSetting.getPhysicsFileName() != '') {
                const physicsFileName = this._modelSetting.getPhysicsFileName();
                fetch(`${this._modelHomeDir}${physicsFileName}`)
                    .then(response => {
                    if (response.ok) {
                        return response.arrayBuffer();
                    }
                    else if (response.status >= 400) {
                        CubismLogError(`Failed to load file ${this._modelHomeDir}${physicsFileName}`);
                        return new ArrayBuffer(0);
                    }
                })
                    .then(arrayBuffer => {
                    this.loadPhysics(arrayBuffer, arrayBuffer.byteLength);
                    // Physics Updaterの追加
                    if (this._physics) {
                        const physicsUpdater = new CubismPhysicsUpdater(this._physics);
                        this._updateScheduler.addUpdatableList(physicsUpdater);
                    }
                    this._state = LoadStep.LoadPose;
                    // callback
                    loadCubismPose();
                });
                this._state = LoadStep.WaitLoadPhysics;
            }
            else {
                this._state = LoadStep.LoadPose;
                // callback
                loadCubismPose();
            }
        };
        // Pose
        const loadCubismPose = () => {
            if (this._modelSetting.getPoseFileName() != '') {
                const poseFileName = this._modelSetting.getPoseFileName();
                fetch(`${this._modelHomeDir}${poseFileName}`)
                    .then(response => {
                    if (response.ok) {
                        return response.arrayBuffer();
                    }
                    else if (response.status >= 400) {
                        CubismLogError(`Failed to load file ${this._modelHomeDir}${poseFileName}`);
                        return new ArrayBuffer(0);
                    }
                })
                    .then(arrayBuffer => {
                    this.loadPose(arrayBuffer, arrayBuffer.byteLength);
                    // Pose Updaterの追加
                    if (this._pose) {
                        const poseUpdater = new CubismPoseUpdater(this._pose);
                        this._updateScheduler.addUpdatableList(poseUpdater);
                    }
                    this._state = LoadStep.SetupEyeBlink;
                    // callback
                    setupEyeBlink();
                });
                this._state = LoadStep.WaitLoadPose;
            }
            else {
                this._state = LoadStep.SetupEyeBlink;
                // callback
                setupEyeBlink();
            }
        };
        // EyeBlink
        const setupEyeBlink = () => {
            if (this._modelSetting.getEyeBlinkParameterCount() > 0) {
                this._eyeBlink = CubismEyeBlink.create(this._modelSetting);
                const eyeBlinkUpdater = new CubismEyeBlinkUpdater(() => this._motionUpdated, this._eyeBlink);
                this._updateScheduler.addUpdatableList(eyeBlinkUpdater);
            }
            this._state = LoadStep.SetupBreath;
            // callback
            setupBreath();
        };
        // Breath
        const setupBreath = () => {
            this._breath = CubismBreath.create();
            const breathParameters = [
                new BreathParameterData(this._idParamAngleX, 0.0, 15.0, 6.5345, 0.5),
                new BreathParameterData(this._idParamAngleY, 0.0, 8.0, 3.5345, 0.5),
                new BreathParameterData(this._idParamAngleZ, 0.0, 10.0, 5.5345, 0.5),
                new BreathParameterData(this._idParamBodyAngleX, 0.0, 4.0, 15.5345, 0.5),
                new BreathParameterData(CubismFramework.getIdManager().getId(CubismDefaultParameterId.ParamBreath), 0.5, 0.5, 3.2345, 1)
            ];
            this._breath.setParameters(breathParameters);
            const breathUpdater = new CubismBreathUpdater(this._breath);
            this._updateScheduler.addUpdatableList(breathUpdater);
            this._state = LoadStep.LoadUserData;
            // callback
            loadUserData();
        };
        // UserData
        const loadUserData = () => {
            if (this._modelSetting.getUserDataFile() != '') {
                const userDataFile = this._modelSetting.getUserDataFile();
                fetch(`${this._modelHomeDir}${userDataFile}`)
                    .then(response => {
                    if (response.ok) {
                        return response.arrayBuffer();
                    }
                    else if (response.status >= 400) {
                        CubismLogError(`Failed to load file ${this._modelHomeDir}${userDataFile}`);
                        return new ArrayBuffer(0);
                    }
                })
                    .then(arrayBuffer => {
                    this.loadUserData(arrayBuffer, arrayBuffer.byteLength);
                    this._state = LoadStep.SetupEyeBlinkIds;
                    // callback
                    setupEyeBlinkIds();
                });
                this._state = LoadStep.WaitLoadUserData;
            }
            else {
                this._state = LoadStep.SetupEyeBlinkIds;
                // callback
                setupEyeBlinkIds();
            }
        };
        // EyeBlinkIds
        const setupEyeBlinkIds = () => {
            const eyeBlinkIdCount = this._modelSetting.getEyeBlinkParameterCount();
            this._eyeBlinkIds.length = eyeBlinkIdCount;
            for (let i = 0; i < eyeBlinkIdCount; ++i) {
                this._eyeBlinkIds[i] = this._modelSetting.getEyeBlinkParameterId(i);
            }
            this._state = LoadStep.SetupLipSyncIds;
            // callback
            setupLipSyncIds();
        };
        // LipSyncIds
        const setupLipSyncIds = () => {
            const lipSyncIdCount = this._modelSetting.getLipSyncParameterCount();
            this._lipSyncIds.length = lipSyncIdCount;
            for (let i = 0; i < lipSyncIdCount; ++i) {
                this._lipSyncIds[i] = this._modelSetting.getLipSyncParameterId(i);
            }
            // LipSync Updaterの追加
            if (this._lipSyncIds.length > 0) {
                const lipSyncUpdater = new CubismLipSyncUpdater(this._lipSyncIds, this._wavFileHandler);
                this._updateScheduler.addUpdatableList(lipSyncUpdater);
            }
            this._state = LoadStep.SetupLook;
            // callback
            setupLook();
        };
        // Look
        const setupLook = () => {
            this._look = CubismLook.create();
            const lookParameters = [];
            for (const tracking of this._pointerTrackingParameters) {
                let parameterIndex = -1;
                for (let index = 0; index < this._model.getParameterCount(); index++) {
                    if (this._model.getParameterId(index).isEqual(tracking.parameterId)) {
                        parameterIndex = index;
                        break;
                    }
                }
                if (parameterIndex < 0)
                    continue;
                const defaultValue = this._model.getParameterDefaultValue(parameterIndex);
                const parameterRange = Math.max(this._model.getParameterMaximumValue(parameterIndex) - defaultValue, defaultValue - this._model.getParameterMinimumValue(parameterIndex));
                const factor = parameterRange * (tracking.impact / 100) * (tracking.reflect ? -1 : 1);
                const parameterId = CubismFramework.getIdManager().getId(tracking.parameterId);
                lookParameters.push(new LookParameterData(parameterId, tracking.source === 'mouseLeftX' ? factor : 0, tracking.source === 'mouseLeftY' ? factor : 0, 0));
            }
            this._look.setParameters(lookParameters);
            const lookUpdater = new CubismLookUpdater(this._look, this._dragManager);
            this._updateScheduler.addUpdatableList(lookUpdater);
            // callback
            finalizeUpdaters();
        };
        // UpdateScheduler最終化処理
        const finalizeUpdaters = () => {
            // 全てのUpdaterが追加されたのでUpdateSchedulerを最終ソート
            this._updateScheduler.sortUpdatableList();
            this._state = LoadStep.SetupLayout;
            // callback
            setupLayout();
        };
        // Layout
        const setupLayout = () => {
            const layout = new Map();
            if (this._modelSetting == null || this._modelMatrix == null) {
                CubismLogError('Failed to setupLayout().');
                return;
            }
            this._modelSetting.getLayoutMap(layout);
            this._modelMatrix.setupFromLayout(layout);
            this._state = LoadStep.LoadMotion;
            // callback
            loadCubismMotion();
        };
        // Motion
        const loadCubismMotion = () => {
            this._state = LoadStep.WaitLoadMotion;
            this._model.saveParameters();
            this._allMotionCount = 0;
            this._motionCount = 0;
            const group = [];
            const motionGroupCount = this._modelSetting.getMotionGroupCount();
            // モーションの総数を求める
            for (let i = 0; i < motionGroupCount; i++) {
                group[i] = this._modelSetting.getMotionGroupName(i);
                this._allMotionCount += this._modelSetting.getMotionCount(group[i]);
            }
            // モーションの読み込み
            for (let i = 0; i < motionGroupCount; i++) {
                this.preLoadMotionGroup(group[i]);
            }
            // モーションがない場合
            if (motionGroupCount == 0) {
                this._state = LoadStep.LoadTexture;
                // 全てのモーションを停止する
                this._motionManager.stopAllMotions();
                this._updating = false;
                this._initialized = true;
                this.createRenderer(this._subdelegate.getCanvas().width, this._subdelegate.getCanvas().height);
                this.setupTextures();
                this.getRenderer().startUp(this._subdelegate.getGlManager().getGl());
                this.getRenderer().loadShaders(LAppDefine.ShaderPath);
            }
        };
    }
    /**
     * テクスチャユニットにテクスチャをロードする
     */
    setupTextures() {
        // iPhoneでのアルファ品質向上のためTypescriptではpremultipliedAlphaを採用
        const usePremultiply = true;
        if (this._state == LoadStep.LoadTexture) {
            // テクスチャ読み込み用
            const textureCount = this._modelSetting.getTextureCount();
            for (let modelTextureNumber = 0; modelTextureNumber < textureCount; modelTextureNumber++) {
                // テクスチャ名が空文字だった場合はロード・バインド処理をスキップ
                if (this._modelSetting.getTextureFileName(modelTextureNumber) == '') {
                    console.log('getTextureFileName null');
                    continue;
                }
                // WebGLのテクスチャユニットにテクスチャをロードする
                let texturePath = this._modelSetting.getTextureFileName(modelTextureNumber);
                texturePath = this._modelHomeDir + texturePath;
                // ロード完了時に呼び出すコールバック関数
                const onLoad = (textureInfo) => {
                    this.getRenderer().bindTexture(modelTextureNumber, textureInfo.id);
                    this._textureCount++;
                    if (this._textureCount >= textureCount) {
                        // ロード完了
                        this._state = LoadStep.CompleteSetup;
                    }
                };
                // 読み込み
                this._subdelegate
                    .getTextureManager()
                    .createTextureFromPngFile(texturePath, usePremultiply, onLoad);
                this.getRenderer().setIsPremultipliedAlpha(usePremultiply);
            }
            this._state = LoadStep.WaitLoadTexture;
        }
    }
    /**
     * レンダラを再構築する
     */
    reloadRenderer() {
        this.deleteRenderer();
        this.createRenderer(this._subdelegate.getCanvas().width, this._subdelegate.getCanvas().height);
        this.setupTextures();
    }
    isReady() {
        return this._state == LoadStep.CompleteSetup;
    }
    /**
     * 更新
     */
    update() {
        if (this._state != LoadStep.CompleteSetup)
            return;
        const deltaTimeSeconds = LAppPal.getDeltaTime();
        this._userTimeSeconds += deltaTimeSeconds;
        //--------------------------------------------------------------------------
        this._model.loadParameters(); // 前回セーブされた状態をロード
        // Reset motion updated flag each frame
        this._motionUpdated = false;
        if (this._motionManager.isFinished()) {
            // モーションの再生がない場合、待機モーションの中からランダムで再生する
            this.startRandomMotion(LAppDefine.MotionGroupIdle, LAppDefine.PriorityIdle);
        }
        else {
            this._motionUpdated = this._motionManager.updateMotion(this._model, deltaTimeSeconds); // モーションを更新
        }
        this._model.saveParameters(); // 状態を保存
        //--------------------------------------------------------------------------
        // UpdateSchedulerによる一括エフェクト更新
        this._updateScheduler.onLateUpdate(this._model, deltaTimeSeconds);
        this._model.update();
    }
    /**
     * 引数で指定したモーションの再生を開始する
     * @param group モーショングループ名
     * @param no グループ内の番号
     * @param priority 優先度
     * @param onFinishedMotionHandler モーション再生終了時に呼び出されるコールバック関数
     * @return 開始したモーションの識別番号を返す。個別のモーションが終了したか否かを判定するisFinished()の引数で使用する。開始できない時は[-1]
     */
    startMotion(group, no, priority, onFinishedMotionHandler, onBeganMotionHandler) {
        if (priority == LAppDefine.PriorityForce) {
            this._motionManager.setReservePriority(priority);
        }
        else if (!this._motionManager.reserveMotion(priority)) {
            if (this._debugMode) {
                LAppPal.printMessage("[APP]can't start motion.");
            }
            return InvalidMotionQueueEntryHandleValue;
        }
        const motionFileName = this._modelSetting.getMotionFileName(group, no);
        // ex) idle_0
        const name = `${group}_${no}`;
        let motion = this._motions.get(name);
        let autoDelete = false;
        if (motion == null) {
            fetch(`${this._modelHomeDir}${motionFileName}`)
                .then(response => {
                if (response.ok) {
                    return response.arrayBuffer();
                }
                else if (response.status >= 400) {
                    CubismLogError(`Failed to load file ${this._modelHomeDir}${motionFileName}`);
                    return new ArrayBuffer(0);
                }
            })
                .then(arrayBuffer => {
                motion = this.loadMotion(arrayBuffer, arrayBuffer.byteLength, null, onFinishedMotionHandler, onBeganMotionHandler, this._modelSetting, group, no, this._motionConsistency);
            });
            if (motion) {
                motion.setEffectIds(this._eyeBlinkIds, this._lipSyncIds);
                autoDelete = true; // 終了時にメモリから削除
            }
            else {
                CubismLogError("Can't start motion {0} .", motionFileName);
                // ロードできなかったモーションのReservePriorityをリセットする
                this._motionManager.setReservePriority(LAppDefine.PriorityNone);
                return InvalidMotionQueueEntryHandleValue;
            }
        }
        else {
            motion.setBeganMotionHandler(onBeganMotionHandler);
            motion.setFinishedMotionHandler(onFinishedMotionHandler);
        }
        //voice
        const voice = this._modelSetting.getMotionSoundFileName(group, no);
        if (voice.localeCompare('') != 0) {
            let path = voice;
            path = this._modelHomeDir + path;
            this._wavFileHandler.start(path);
        }
        if (this._debugMode) {
            LAppPal.printMessage(`[APP]start motion: [${group}_${no}]`);
        }
        return this._motionManager.startMotionPriority(motion, autoDelete, priority);
    }
    /**
     * ランダムに選ばれたモーションの再生を開始する。
     * @param group モーショングループ名
     * @param priority 優先度
     * @param onFinishedMotionHandler モーション再生終了時に呼び出されるコールバック関数
     * @return 開始したモーションの識別番号を返す。個別のモーションが終了したか否かを判定するisFinished()の引数で使用する。開始できない時は[-1]
     */
    startRandomMotion(group, priority, onFinishedMotionHandler, onBeganMotionHandler) {
        if (this._modelSetting.getMotionCount(group) == 0) {
            return InvalidMotionQueueEntryHandleValue;
        }
        const no = Math.floor(Math.random() * this._modelSetting.getMotionCount(group));
        return this.startMotion(group, no, priority, onFinishedMotionHandler, onBeganMotionHandler);
    }
    /**
     * 引数で指定した表情モーションをセットする
     *
     * @param expressionId 表情モーションのID
     */
    setExpression(expressionId) {
        const motion = this._expressions.get(expressionId);
        if (this._debugMode) {
            LAppPal.printMessage(`[APP]expression: [${expressionId}]`);
        }
        if (motion != null) {
            this._expressionManager.startMotion(motion, false);
        }
        else {
            if (this._debugMode) {
                LAppPal.printMessage(`[APP]expression[${expressionId}] is null`);
            }
        }
    }
    /**
     * ランダムに選ばれた表情モーションをセットする
     */
    setRandomExpression() {
        if (this._expressions.size == 0) {
            return;
        }
        const no = Math.floor(Math.random() * this._expressions.size);
        for (let i = 0; i < this._expressions.size; i++) {
            if (i == no) {
                // const name: string = this._expressions._keyValues[i].first;
                const expressionsArray = [...this._expressions.entries()];
                const name = expressionsArray[i][0];
                this.setExpression(name);
                return;
            }
        }
    }
    /**
     * イベントの発火を受け取る
     */
    motionEventFired(eventValue) {
        CubismLogInfo('{0} is fired on LAppModel!!', eventValue);
    }
    /**
     * 当たり判定テスト
     * 指定ＩＤの頂点リストから矩形を計算し、座標をが矩形範囲内か判定する。
     *
     * @param hitArenaName  当たり判定をテストする対象のID
     * @param x             判定を行うX座標
     * @param y             判定を行うY座標
     */
    hitTest(hitArenaName, x, y) {
        // 透明時は当たり判定無し。
        if (this._opacity < 1) {
            return false;
        }
        const count = this._modelSetting.getHitAreasCount();
        for (let i = 0; i < count; i++) {
            if (this._modelSetting.getHitAreaName(i) == hitArenaName) {
                const drawId = this._modelSetting.getHitAreaId(i);
                return this.isHit(drawId, x, y);
            }
        }
        return false;
    }
    /**
     * モーションデータをグループ名から一括でロードする。
     * モーションデータの名前は内部でModelSettingから取得する。
     *
     * @param group モーションデータのグループ名
     */
    preLoadMotionGroup(group) {
        for (let i = 0; i < this._modelSetting.getMotionCount(group); i++) {
            const motionFileName = this._modelSetting.getMotionFileName(group, i);
            // ex) idle_0
            const name = `${group}_${i}`;
            if (this._debugMode) {
                LAppPal.printMessage(`[APP]load motion: ${motionFileName} => [${name}]`);
            }
            fetch(`${this._modelHomeDir}${motionFileName}`)
                .then(response => {
                if (response.ok) {
                    return response.arrayBuffer();
                }
                else if (response.status >= 400) {
                    CubismLogError(`Failed to load file ${this._modelHomeDir}${motionFileName}`);
                    return new ArrayBuffer(0);
                }
            })
                .then(arrayBuffer => {
                const tmpMotion = this.loadMotion(arrayBuffer, arrayBuffer.byteLength, name, null, null, this._modelSetting, group, i, this._motionConsistency);
                if (tmpMotion != null) {
                    tmpMotion.setEffectIds(this._eyeBlinkIds, this._lipSyncIds);
                    if (this._motions.get(name) != null) {
                        ACubismMotion.delete(this._motions.get(name));
                    }
                    this._motions.set(name, tmpMotion);
                    this._motionCount++;
                }
                else {
                    // loadMotionできなかった場合はモーションの総数がずれるので1つ減らす
                    this._allMotionCount--;
                }
                if (this._motionCount >= this._allMotionCount) {
                    this._state = LoadStep.LoadTexture;
                    // 全てのモーションを停止する
                    this._motionManager.stopAllMotions();
                    this._updating = false;
                    this._initialized = true;
                    this.createRenderer(this._subdelegate.getCanvas().width, this._subdelegate.getCanvas().height);
                    this.setupTextures();
                    this.getRenderer().startUp(this._subdelegate.getGlManager().getGl());
                    this.getRenderer().loadShaders(LAppDefine.ShaderPath);
                }
            });
        }
    }
    /**
     * すべてのモーションデータを解放する。
     */
    releaseMotions() {
        this._motions.clear();
    }
    /**
     * 全ての表情データを解放する。
     */
    releaseExpressions() {
        this._expressions.clear();
    }
    /**
     * モデルを描画する処理。モデルを描画する空間のView-Projection行列を渡す。
     */
    doDraw() {
        if (this._model == null)
            return;
        // キャンバスサイズを渡す
        const canvas = this._subdelegate.getCanvas();
        const viewport = [0, 0, canvas.width, canvas.height];
        this.getRenderer().setRenderState(this._subdelegate.getFrameBuffer(), viewport);
        this.getRenderer().drawModel(LAppDefine.ShaderPath);
    }
    /**
     * モデルを描画する処理。モデルを描画する空間のView-Projection行列を渡す。
     */
    draw(matrix) {
        if (this._model == null) {
            return;
        }
        // 各読み込み終了後
        if (this._state == LoadStep.CompleteSetup) {
            matrix.multiplyByMatrix(this._modelMatrix);
            this.getRenderer().setMvpMatrix(matrix);
            this.doDraw();
        }
    }
    async hasMocConsistencyFromFile() {
        CSM_ASSERT(this._modelSetting.getModelFileName().localeCompare(``));
        // CubismModel
        if (this._modelSetting.getModelFileName() != '') {
            const modelFileName = this._modelSetting.getModelFileName();
            const response = await fetch(`${this._modelHomeDir}${modelFileName}`);
            const arrayBuffer = await response.arrayBuffer();
            this._consistency = CubismMoc.hasMocConsistency(arrayBuffer);
            if (!this._consistency) {
                CubismLogInfo('Inconsistent MOC3.');
            }
            else {
                CubismLogInfo('Consistent MOC3.');
            }
            return this._consistency;
        }
        else {
            LAppPal.printMessage('Model data does not exist.');
        }
    }
    setSubdelegate(subdelegate) {
        this._subdelegate = subdelegate;
    }
    setPointerTracking(parameters) {
        this._pointerTrackingParameters = parameters;
    }
    /**
     * デストラクタに相当する処理のオーバーライド
     */
    release() {
        if (this._look) {
            CubismLook.delete(this._look);
            this._look = null;
        }
        if (this._updateScheduler) {
            this._updateScheduler.release();
        }
        super.release();
    }
    /**
     * コンストラクタ
     */
    constructor() {
        super();
        this._modelSetting = null;
        this._modelHomeDir = null;
        this._userTimeSeconds = 0.0;
        this._eyeBlinkIds = new Array();
        this._lipSyncIds = new Array();
        this._motions = new Map();
        this._expressions = new Map();
        this._hitArea = new Array();
        this._userArea = new Array();
        this._idParamAngleX = CubismFramework.getIdManager().getId(CubismDefaultParameterId.ParamAngleX);
        this._idParamAngleY = CubismFramework.getIdManager().getId(CubismDefaultParameterId.ParamAngleY);
        this._idParamAngleZ = CubismFramework.getIdManager().getId(CubismDefaultParameterId.ParamAngleZ);
        this._idParamBodyAngleX = CubismFramework.getIdManager().getId(CubismDefaultParameterId.ParamBodyAngleX);
        if (LAppDefine.MOCConsistencyValidationEnable) {
            this._mocConsistency = true;
        }
        if (LAppDefine.MotionConsistencyValidationEnable) {
            this._motionConsistency = true;
        }
        this._state = LoadStep.LoadAssets;
        this._expressionCount = 0;
        this._textureCount = 0;
        this._motionCount = 0;
        this._allMotionCount = 0;
        this._wavFileHandler = new LAppWavFileHandler();
        this._consistency = false;
        this._look = null;
        this._pointerTrackingParameters = [];
        this._updateScheduler = new CubismUpdateScheduler();
        this._motionUpdated = false;
    }
}
