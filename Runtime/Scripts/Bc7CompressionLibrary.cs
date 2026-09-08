using UdonSharp;
using UnityEngine;
using VRC.SDK3.Rendering;
using VRC.SDKBase;
using VRC.Udon;

namespace HDAssets.ImageCompress.Bc7
{
    // VRChat/UdonSharp上でTextureをBC7 mode 6のbyte列へ圧縮するライブラリ
    // 処理の流れ:
    // 1. 圧縮要求を受けて入力と実行状態を検証する
    // 2. 作業用RenderTextureを確保し、GPUで候補生成・探索・最良候補選択・128bit packingを段階実行する
    // 3. GPU完了を確認してRGBA32搬送面をbyte[]へ非同期readbackする
    // 4. 結果と状態を更新して呼び出し元へ完了または失敗イベントを送る
    // 必要に応じてBC7 byte列から確認用Textureを生成できる
    // GPU出力のRGBA32は1pixelのRGBAへBC7の連続4byteを格納する一時的なbyte搬送面として使う
    // X方向4pixelでBC7 4x4 block 1個分の16byteとなるため、出力寸法はceil(width/4)*4 × ceil(height/4)になる
    // 出力modeはRGBAを保持できるBC7 mode 6固定とする
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class Bc7CompressionLibrary : UdonSharpBehaviour
    {
        // BC7ブロックの横幅をpixel単位で示す定数
        public const int BlockWidth = 4;
        // BC7ブロックの縦幅をpixel単位で示す定数
        public const int BlockHeight = 4;
        // BC7ブロック1個あたりのbyte数
        public const int BytesPerBlock = 16;
        // 無効な処理handleを示す値
        public const int InvalidHandleId = -1;
        // 対象handleの状態を判定できないことを示す値
        public const int TaskStateUnknown = 0;
        // 処理開始待ち状態を示す値
        public const int TaskStatePending = 1;
        // 処理実行中状態を示す値
        public const int TaskStateRunning = 2;
        // 処理成功状態を示す値
        public const int TaskStateSucceeded = 3;
        // 処理失敗状態を示す値
        public const int TaskStateFailed = 4;
        // 処理キャンセル状態を示す値
        public const int TaskStateCancelled = 5;
        // 進行段階を判定できないことを示す値
        public const int ProgressStageUnknown = 0;
        // 処理開始待ち段階を示す値
        public const int ProgressStagePending = 1;
        // 入力とGPUリソースの準備段階を示す値
        public const int ProgressStagePreparing = 2;
        // GPU圧縮処理段階を示す値
        public const int ProgressStageProcessing = 3;
        // GPU出力のreadback段階を示す値
        public const int ProgressStageReadback = 4;
        // 結果確定と後処理段階を示す値
        public const int ProgressStageFinalizing = 5;
        // 全処理完了段階を示す値
        public const int ProgressStageComplete = 6;
        // 圧縮成功時に要求元へ送るイベント名
        public const string CompressionSucceededEventName = "_HandleBc7CompressComplete";
        // 圧縮失敗時に要求元へ送るイベント名
        public const string CompressionFailedEventName = "_HandleBc7CompressFailed";
        // 圧縮warmup成功時に要求元へ送るイベント名
        public const string CompressionWarmupSucceededEventName = "_HandleBc7CompressWarmupComplete";
        // 圧縮warmup失敗時に要求元へ送るイベント名
        public const string CompressionWarmupFailedEventName = "_HandleBc7CompressWarmupFailed";
        // 生成するBC7圧縮候補の総数
        private const int TotalCandidateCount = 16;
        // 1回のGPU処理で評価する圧縮候補数
        private const int CandidateBatchSize = 2;
        // 全候補を処理するためのbatch数
        private const int CandidateBatchCount = TotalCandidateCount / CandidateBatchSize;
        // 1ブロックの候補endpoint格納に使うpixel数
        private const int CandidateEndpointPixelsPerBlock = CandidateBatchSize * 2;
        // 1ブロックの最良候補格納に使うpixel数
        private const int BestCandidatePixelsPerBlock = 3;
        // 候補endpoint生成に使用するShader pass番号
        private const int CandidateEndpointsBatch0Pass = 0;
        // 最小二乗再適合に使用するShader pass番号
        private const int LeastSquaresPass = 0;
        // endpoint範囲探索に使用するShader pass番号
        private const int ScaleSearchPass = 0;
        // endpoint局所探索に使用するShader pass番号
        private const int NudgeSearchPass = 0;
        // 最良候補選択に使用するShader pass番号
        private const int BestCandidatePass = 0;
        // BC7 byte列の格納に使用するShader pass番号
        private const int PackBytesPass = 1;
        // GPU完了確認用marker Textureの一辺のpixel数
        private const int GpuFenceMarkerSize = 4;
        // 候補batchごとに実行する圧縮pass数
        private const int CompressionPassesPerCandidateBatch = 6;
        // GPU完了確認を挟まず連続実行できる最大batch数
        private const int GpuFenceBatchLimit = CandidateBatchCount * CompressionPassesPerCandidateBatch + 1;
        // Material保持用Rendererに設定するMaterial数
        private const int RuntimeMaterialCount = 5;


        // 圧縮対象として外部から設定する入力Texture
        [HideInInspector, System.NonSerialized] public Texture sourceTexture;
        // 入力TextureがShader上でsRGB領域の値を返すかを示す設定
        [HideInInspector, System.NonSerialized] public bool sourceTextureSrgb = true;

        // 圧縮対象画像の横幅
        [HideInInspector, System.NonSerialized] public int sourceWidth;
        // 圧縮対象画像の縦幅
        [HideInInspector, System.NonSerialized] public int sourceHeight;

        [Header("Options")]

        // 実行時にGPU処理用Materialを複製するための描画無効Renderer
        [SerializeField] private MeshRenderer materialInstanceRenderer;
        // 圧縮候補生成に使用する実行時Material
        private Material gpuCompressMaterial;
        // 候補endpointの再適合に使用する実行時Material
        private Material gpuRefineMaterial;
        // endpoint範囲探索に使用する実行時Material
        private Material gpuScaleSearchMaterial;
        // endpoint局所探索に使用する実行時Material
        private Material gpuNudgeSearchMaterial;
        // 最良候補選択とBC7 byte列生成に使用する実行時Material
        private Material gpuFinalizeMaterial;
        // gpuOutputTextureはBC7 byte列のreadback用。最終表示用のARGB32 Textureではない
        [SerializeField] private RenderTexture gpuOutputTexture;
        // 候補生成、段階的な候補更新、最良候補を受け渡すGPU上の一時作業領域
        [SerializeField] private RenderTexture gpuCandidateTexture;
        [SerializeField] private RenderTexture gpuCandidateScratchTexture;
        [SerializeField] private RenderTexture gpuCandidateFinalTexture;
        [SerializeField] private RenderTexture gpuBestCandidateTexture;
        [SerializeField] private RenderTexture gpuBestCandidateScratchTexture;
        [SerializeField] private bool autoCreateGpuOutputTexture = true;
        [SerializeField] private bool autoCreateGpuWorkingTextures = true;
        [SerializeField] private bool autoCreateCompressedTexture;

        // BC7へ格納するRGB値をsRGB領域として扱うかを示す設定
        [HideInInspector, System.NonSerialized] public bool encodeSrgb = true;
        [SerializeField] private bool destroySourceTextureOnComplete;
        [SerializeField] private bool releaseAutoCreatedGpuWorkingTexturesOnComplete;
        [SerializeField] private bool clearCompressedBytesAfterTextureCreated;
        // 圧縮処理とwarmupのtimeout秒数
        [Min(1f)] public float operationTimeoutSeconds = 60f;
        [SerializeField] private UdonBehaviour completionEventReceiver;
        [SerializeField] private string completionEventName = "_HandleICCompressionComplete";
        [SerializeField] private string failedEventName = "_HandleICCompressionFailed";


        // 圧縮後のnative BC7 byte列
        [HideInInspector, System.NonSerialized] public byte[] compressedBytes;
        // 必要に応じて生成する確認用BC7 Texture
        [HideInInspector, System.NonSerialized] public Texture compressedTexture;
        // 圧縮結果の有効byte数
        [HideInInspector, System.NonSerialized] public int compressedByteCount;
        // 横方向のBC7ブロック数
        [HideInInspector, System.NonSerialized] public int blockCountX;
        // 縦方向のBC7ブロック数
        [HideInInspector, System.NonSerialized] public int blockCountY;
        // 圧縮処理が進行中かを示すflag
        [HideInInspector, System.NonSerialized] public bool compressionPending;
        // キャンセル後のGPU readback完了待ちかを示すflag
        [HideInInspector, System.NonSerialized] public bool cancelledReadbackPending;
        // 直近の圧縮処理が成功したかを示すflag
        [HideInInspector, System.NonSerialized] public bool compressionComplete;
        // 直近の圧縮処理が失敗したかを示すflag
        [HideInInspector, System.NonSerialized] public bool compressionFailed;
        // 圧縮warmupが実行中かを示すflag
        [HideInInspector, System.NonSerialized] public bool warmupRunning;
        // 直近の圧縮warmupが成功したかを示すflag
        [HideInInspector, System.NonSerialized] public bool warmupComplete;
        // 直近の圧縮warmupが失敗したかを示すflag
        [HideInInspector, System.NonSerialized] public bool warmupFailed;
        // GPU圧縮開始からreadback完了までの経過時間
        [HideInInspector, System.NonSerialized] public float readbackElapsedMs;
        // 圧縮要求から完了までの総経過時間
        [HideInInspector, System.NonSerialized] public float totalElapsedMs;
        // 直近の処理状態を表すmessage
        [HideInInspector, System.NonSerialized] public string status;
        // 実行開始時に固定した出力色領域設定
        [HideInInspector, System.NonSerialized] public bool activeEncodeSrgb;
        // 実行開始時に固定した入力色領域設定
        [HideInInspector, System.NonSerialized] public bool activeSourceSrgb;

        private VRCAsyncGPUReadbackRequest gpuReadbackRequest;
        private VRCAsyncGPUReadbackRequest gpuFenceReadbackRequest;
        private RenderTexture gpuFenceMarkerTexture;
        private RenderTexture gpuFenceSourceTexture;
        private RenderTexture gpuUnfencedBatchSourceTexture;
        private bool gpuBlitSubmittedSinceSchedule;
        private int gpuUnfencedBatchCount;
        private bool gpuFenceMarkerQueued;
        private bool gpuFenceReadbackQueued;
        private bool gpuFenceReadbackPending;
        private string gpuFenceContinuationEvent = "";
        private bool gpuOutputTextureWasAutoCreated;
        private bool gpuCandidateTextureWasAutoCreated;
        private bool gpuCandidateScratchTextureWasAutoCreated;
        private bool gpuCandidateFinalTextureWasAutoCreated;
        private bool gpuBestCandidateTextureWasAutoCreated;
        private bool gpuBestCandidateScratchTextureWasAutoCreated;
        private float compressionStartedAt;
        private int gpuPassStep;
        private int gpuCandidateBatchIndex;
        private bool gpuReadbackStarted;
        private bool gpuReadbackQueued;
        private bool activeRunIsWarmup;
        private Texture sourceTextureBeforeWarmup;
        private RenderTexture warmupSourceTexture;
        private bool sourceTextureSrgbBeforeWarmup;
        private bool encodeSrgbBeforeWarmup;
        private int nextCompressionHandleId;
        private int activeCompressionHandleId = InvalidHandleId;
        private int completedCompressionHandleId = InvalidHandleId;
        private int failedCompressionHandleId = InvalidHandleId;
        private int cancelledCompressionHandleId = InvalidHandleId;
        private UdonBehaviour compressionRequestReceiver;
        private int nextCompressionWarmupHandleId;
        private int activeCompressionWarmupHandleId = InvalidHandleId;
        private int completedCompressionWarmupHandleId = InvalidHandleId;
        private int failedCompressionWarmupHandleId = InvalidHandleId;
        private int cancelledCompressionWarmupHandleId = InvalidHandleId;
        private UdonBehaviour compressionWarmupRequestReceiver;
        private float compressionWarmupRequestedAt;

        // 実行中の圧縮handle ID
        public int ActiveCompressionHandleId { get { return activeCompressionHandleId; } }
        // 最後に成功した圧縮handle ID
        public int CompletedCompressionHandleId { get { return completedCompressionHandleId; } }
        // 最後に失敗した圧縮handle ID
        public int FailedCompressionHandleId { get { return failedCompressionHandleId; } }
        // 圧縮成功時のBC7 byte列
        public byte[] CompressionResultBytes { get { return compressedBytes; } }
        // 圧縮結果の元画像幅
        public int CompressionResultWidth { get { return sourceWidth; } }
        // 圧縮結果の元画像高さ
        public int CompressionResultHeight { get { return sourceHeight; } }
        // 実行中の圧縮warmup handle ID
        public int ActiveCompressionWarmupHandleId { get { return activeCompressionWarmupHandleId; } }
        // 最後に成功した圧縮warmup handle ID
        public int CompletedCompressionWarmupHandleId { get { return completedCompressionWarmupHandleId; } }
        // 最後に失敗した圧縮warmup handle ID
        public int FailedCompressionWarmupHandleId { get { return failedCompressionWarmupHandleId; } }

        // Material保持用Rendererからこのライブラリ専用の複製Materialを取得する
        // 受付成功後の専用入力と複製Materialは、このライブラリが解放まで管理する
        private Texture ownedInputTexture;
        private Texture borrowedCopySource;
        private bool inputCopyPending;
        // キャンセル前に予約された遅延イベントが次の要求へ重なっても転写は一度だけ行う
        private bool inputCopySubmitted;
        private int copiedInputHandleId = InvalidHandleId;
        private bool failureNotificationPending;
        private bool cancellationNotificationPending;
        private UdonBehaviour cancellationReceiver;
        private bool disposalRequested;
        private bool disposed;
        private Material[] ownedRuntimeMaterials;
        // キャンセル要求済みでも、GPU完了までは新しい入力を受け付けない
        public const int TaskStateCancelling = 6;
        public bool IsBusy { get { return compressionPending || warmupRunning || inputCopyPending
            || cancelledReadbackPending || activeCompressionHandleId != InvalidHandleId
            || activeCompressionWarmupHandleId != InvalidHandleId; } }
        public bool CanAcceptRequest { get { return !IsBusy && !disposalRequested; } }
        public bool IsDisposed { get { return disposed; } }

        private void Start()
        {
            InitializeRuntimeMaterials();
        }

        // Materialスロットを各GPU処理へ割り当てる
        private void InitializeRuntimeMaterials()
        {
            if (disposalRequested || ownedRuntimeMaterials != null || materialInstanceRenderer == null)
            {
                return;
            }

            materialInstanceRenderer.enabled = false;
            Material[] runtimeMaterials = materialInstanceRenderer.materials;
            ownedRuntimeMaterials = runtimeMaterials;
            if (runtimeMaterials == null || runtimeMaterials.Length != RuntimeMaterialCount)
            {
                return;
            }

            gpuCompressMaterial = runtimeMaterials[0];
            gpuRefineMaterial = runtimeMaterials[1];
            gpuScaleSearchMaterial = runtimeMaterials[2];
            gpuNudgeSearchMaterial = runtimeMaterials[3];
            gpuFinalizeMaterial = runtimeMaterials[4];
        }

        // 圧縮要求を登録し、処理を識別するhandle IDを返す
        // 専用Textureの所有権を渡す。受付失敗なら所有権は呼び側に残る
        public int RequestCompressionOwned(Texture inputTexture, bool inputSrgb, bool outputSrgb, UdonBehaviour eventReceiver)
        {
            if (!CanAcceptRequest) return InvalidHandleId;
            int handle = RegisterCompressionRequest(inputTexture, inputSrgb, outputSrgb, eventReceiver);
            if (handle != InvalidHandleId) ownedInputTexture = inputTexture;
            return handle;
        }

        // 共有Textureをコピーする。元Textureは取り込み完了または処理終了まで保持する
        // 元Textureの所有権は移らず、コピーRTだけをライブラリが解放する
        public int RequestCompressionCopy(Texture inputTexture, bool inputSrgb, bool outputSrgb, UdonBehaviour eventReceiver)
        {
            if (!CanAcceptRequest) return InvalidHandleId;
            int handle = RegisterCompressionRequest(inputTexture, inputSrgb, outputSrgb, eventReceiver);
            if (handle != InvalidHandleId)
            {
                borrowedCopySource = inputTexture;
                inputCopyPending = true;
                inputCopySubmitted = false;
            }
            return handle;
        }

        // trueなら元Textureを解放できる。まだコピーしていない失敗・キャンセルは終端状態で判断する
        public bool IsInputCopyComplete(int handleId) { return handleId != InvalidHandleId && handleId == copiedInputHandleId; }

        // 成功結果を一度だけ取り出し、ライブラリの参照を外す
        public byte[] TakeCompressionResult(int handleId)
        {
            if (handleId == InvalidHandleId || handleId != completedCompressionHandleId) return null;
            byte[] result = compressedBytes;
            compressedBytes = null;
            return result;
        }

        private int RegisterCompressionRequest(
            Texture inputTexture,
            bool inputSrgb,
            bool outputSrgb,
            UdonBehaviour eventReceiver)
        {
            if (inputTexture == null || eventReceiver == null || compressionPending || warmupRunning
                || cancelledReadbackPending || activeCompressionHandleId != InvalidHandleId
                || activeCompressionWarmupHandleId != InvalidHandleId)
            {
                SetStatus(inputTexture == null
                    ? "BC7 compression input texture is missing."
                    : eventReceiver == null
                        ? "BC7 compression event receiver is missing."
                        : "BC7 compression is already pending.");
                return InvalidHandleId;
            }

            int handleId = GenerateCompressionHandleId();
            activeCompressionHandleId = handleId;
            completedCompressionHandleId = InvalidHandleId;
            failedCompressionHandleId = InvalidHandleId;
            cancelledCompressionHandleId = InvalidHandleId;
            compressionRequestReceiver = eventReceiver;
            sourceTexture = inputTexture;
            copiedInputHandleId = InvalidHandleId;
            sourceTextureSrgb = inputSrgb;
            encodeSrgb = outputSrgb;
            SendCustomEventDelayedFrames(nameof(_BeginRequestedCompression), 1);
            return handleId;
        }

        // 次frameへ遅延した圧縮要求を開始する
        public void _BeginRequestedCompression()
        {
            if (activeCompressionHandleId == InvalidHandleId)
            {
                return;
            }
            if (inputCopyPending) { PrepareInputCopy(); return; }
            BeginCompression(false);
        }

        // ASTC 4x4等と共通のblock compressor APIから呼ばれる入口
        // 確保・Blit・Readback要求を別frameへ分ける。色領域は入力の指定を引き継ぐ
        private void PrepareInputCopy()
        {
            if (ownedInputTexture != null) return;
            if (borrowedCopySource == null || borrowedCopySource.width > 8192 || borrowedCopySource.height > 8192
                || (long)borrowedCopySource.width * borrowedCopySource.height > 16777216L)
            {
                FailCompression("Invalid input copy dimensions.");
                return;
            }
            RenderTextureDescriptor descriptor = new RenderTextureDescriptor(borrowedCopySource.width, borrowedCopySource.height, RenderTextureFormat.ARGB32, 0);
            descriptor.sRGB = sourceTextureSrgb;
            descriptor.msaaSamples = 1;
            descriptor.useMipMap = false;
            descriptor.autoGenerateMips = false;
            RenderTexture snapshot = new RenderTexture(descriptor);
            snapshot.name = "IC_Bc7_OwnedInput";
            snapshot.Create();
            ownedInputTexture = snapshot;
            compressionStartedAt = Time.realtimeSinceStartup;
            if (!snapshot.IsCreated()) { FailCompression("Input copy RenderTexture creation failed."); return; }
            SendCustomEventDelayedFrames(nameof(_CopyInputTexture), 1);
        }

        // Blitの発行だけでは元Textureを解放できないため、完了fenceへ進める
        public void _CopyInputTexture()
        {
            if (!inputCopyPending || inputCopySubmitted || activeCompressionHandleId == InvalidHandleId) return;
            if (borrowedCopySource == null || ownedInputTexture == null) { FailCompression("Input copy source was destroyed."); return; }
            inputCopySubmitted = true;
            TrackedBlit(borrowedCopySource, (RenderTexture)ownedInputTexture);
            ScheduleAfterGpuBatch(nameof(_CompleteInputCopy));
        }

        // 転写がGPU上で完了した後、元Textureとの関係を切って通知する
        public void _CompleteInputCopy()
        {
            if (!inputCopyPending || !inputCopySubmitted || activeCompressionHandleId == InvalidHandleId) return;
            inputCopyPending = false;
            borrowedCopySource = null;
            copiedInputHandleId = activeCompressionHandleId;
            sourceTexture = ownedInputTexture;
            SendCustomEventDelayedFrames(nameof(_BeginRequestedCompression), 1);
            if (compressionRequestReceiver != null) compressionRequestReceiver.SendCustomEvent("_HandleBc7InputCopied");
        }

        // 1x1の組み込みTextureで全passを通し、初回の実圧縮へShader compileの負荷を持ち越さない
        // 呼び出し側の入力は退避して復元し、warmup結果のGPU readbackと完了イベントは行わない
        public int RequestCompressionWarmup(UdonBehaviour eventReceiver)
        {
            if (disposalRequested || eventReceiver == null || warmupRunning || compressionPending || cancelledReadbackPending
                || activeCompressionHandleId != InvalidHandleId
                || activeCompressionWarmupHandleId != InvalidHandleId)
            {
                SetStatus(eventReceiver == null
                    ? "BC7 compression warmup event receiver is missing."
                    : "BC7 compression or warmup is already pending.");
                return InvalidHandleId;
            }

            int handleId = GenerateCompressionWarmupHandleId();
            activeCompressionWarmupHandleId = handleId;
            completedCompressionWarmupHandleId = InvalidHandleId;
            failedCompressionWarmupHandleId = InvalidHandleId;
            cancelledCompressionWarmupHandleId = InvalidHandleId;
            compressionWarmupRequestReceiver = eventReceiver;
            compressionWarmupRequestedAt = Time.realtimeSinceStartup;
            warmupComplete = false;
            warmupFailed = false;
            SendCustomEventDelayedFrames(nameof(_BeginRequestedCompressionWarmup), 1);
            return handleId;
        }

        // 登録済みの圧縮warmup要求を次frameから開始する
        public void _BeginRequestedCompressionWarmup()
        {
            if (activeCompressionWarmupHandleId == InvalidHandleId)
            {
                return;
            }
            BeginCompressionWarmupInternal();
        }

        // 共通APIから圧縮warmupを開始する
        public void _BeginWarmup()
        {
            if (!CanAcceptRequest) return;
            BeginCompressionWarmupInternal();
        }

        // 入力状態を退避してwarmup用Textureを準備する
        private void BeginCompressionWarmupInternal()
        {
            if (warmupRunning || compressionPending || cancelledReadbackPending
                || activeCompressionHandleId != InvalidHandleId)
            {
                if (activeCompressionWarmupHandleId != InvalidHandleId)
                {
                    FailCompressionWarmup("BC7 compression warmup did not start.");
                }
                return;
            }

            sourceTextureBeforeWarmup = sourceTexture;
            sourceTextureSrgbBeforeWarmup = sourceTextureSrgb;
            encodeSrgbBeforeWarmup = encodeSrgb;
            warmupSourceTexture = new RenderTexture(
                BlockWidth,
                BlockHeight,
                0,
                RenderTextureFormat.ARGB32);
            warmupSourceTexture.Create();
            if (!warmupSourceTexture.IsCreated())
            {
                FailCompressionWarmup("BC7 compression warmup source creation failed.");
                return;
            }
            sourceTexture = warmupSourceTexture;
            sourceTextureSrgb = true;
            encodeSrgb = true;
            warmupRunning = true;
            warmupFailed = false;
            activeRunIsWarmup = true;
            SendCustomEventDelayedFrames(nameof(_PrepareCompressionWarmupSource), 1);
        }

        // warmup用Textureへ基準色を書き込みGPU完了を待つ
        public void _PrepareCompressionWarmupSource()
        {
            if (!warmupRunning || warmupSourceTexture == null)
            {
                return;
            }

            TrackedBlit(Texture2D.whiteTexture, warmupSourceTexture);
            ScheduleAfterGpuBatch(nameof(_StartCompressionWarmup));
        }

        // 準備済みTextureを使って圧縮warmupを開始する
        public void _StartCompressionWarmup()
        {
            if (!warmupRunning || warmupSourceTexture == null)
            {
                return;
            }

            BeginCompression(true);
            if (warmupRunning && !compressionPending)
            {
                FailCompressionWarmup("BC7 compression warmup did not start.");
            }
        }

        // 圧縮要求とwarmupのtimeoutを監視する
        private void Update()
        {
            if (activeCompressionWarmupHandleId != InvalidHandleId && !compressionPending
                && compressionWarmupRequestedAt > 0f
                && Time.realtimeSinceStartup - compressionWarmupRequestedAt > Mathf.Max(1f, operationTimeoutSeconds))
            {
                FailCompressionWarmup("BC7 compression warmup request timed out.");
            }
            if ((!compressionPending && !inputCopyPending) || compressionStartedAt <= 0f
                || Time.realtimeSinceStartup - compressionStartedAt <= Mathf.Max(1f, operationTimeoutSeconds))
            {
                return;
            }

            FailActiveOperation(activeRunIsWarmup
                ? "warmup timed out."
                : "compression timed out.");
        }

        // 入力検証とGPUリソース準備を行い圧縮passを開始する
        private void BeginCompression(bool isWarmup)
        {
            if (cancelledReadbackPending)
            {
                SetStatus("BC7 GPU readback cancellation is still pending.");
                return;
            }
            // 同じinstanceで二重にGPU readbackを開始すると結果を識別できないため、実行中の再入を拒否する
            if (compressionPending)
            {
                SetStatus("BC7 GPU compression is already pending.");
                return;
            }

            // 前回結果を先に破棄し、途中失敗でも古い成功データを新しい結果として扱わないようにする
            compressionPending = false;
            compressionComplete = false;
            compressionFailed = false;
            compressedByteCount = 0;
            blockCountX = 0;
            blockCountY = 0;
            compressedBytes = null;
            _ClearCompressedTexture();
            readbackElapsedMs = 0f;
            totalElapsedMs = 0f;
            compressionStartedAt = 0f;
            activeSourceSrgb = false;
            activeRunIsWarmup = isWarmup;
            gpuPassStep = 0;
            gpuCandidateBatchIndex = 0;
            gpuReadbackStarted = false;
            gpuReadbackQueued = false;
            CancelGpuFence();

            // Shader実行前に必須参照と画像サイズを検証し、不正なRenderTexture確保を防ぐ
            if (sourceTexture == null)
            {
                FailActiveOperation("sourceTexture is missing.");
                return;
            }

            if (gpuCompressMaterial == null || gpuRefineMaterial == null
                || gpuScaleSearchMaterial == null || gpuNudgeSearchMaterial == null
                || gpuFinalizeMaterial == null)
            {
                FailActiveOperation("BC7 GPU stage material is missing.");
                return;
            }

            sourceWidth = sourceTexture.width;
            sourceHeight = sourceTexture.height;
            if (sourceWidth <= 0 || sourceHeight <= 0)
            {
                FailActiveOperation("sourceTexture size is invalid.");
                return;
            }

            if (!EnsureGpuOutputTexture(sourceWidth, sourceHeight))
            {
                FailActiveOperation("gpuOutputTexture is missing or invalid.");
                return;
            }

            blockCountX = GetBlockCountX(sourceWidth);
            blockCountY = GetBlockCountY(sourceHeight);
            // 端数pixelを含む場合も4x4 block単位で切り上げる。payloadは常にblock数×16byte
            compressedByteCount = GetCompressedByteCount(sourceWidth, sourceHeight);
            if (compressedByteCount <= 0)
            {
                FailActiveOperation("compressed byte count is invalid.");
                return;
            }
            if (!EnsureGpuWorkingTextures(sourceWidth, sourceHeight))
            {
                FailActiveOperation("BC7 GPU working textures are missing or invalid.");
                return;
            }

            // 非同期処理中に設定が変わっても結果の解釈が変わらないよう、開始時の設定を固定する
            activeEncodeSrgb = encodeSrgb;
            activeSourceSrgb = sourceTextureSrgb;
            // 各passが同じblock座標と作業Texture配置を再構成できるよう、実寸と作業面幅を明示する
            SetupStageMaterial(gpuCompressMaterial);
            SetupStageMaterial(gpuRefineMaterial);
            SetupStageMaterial(gpuScaleSearchMaterial);
            SetupStageMaterial(gpuNudgeSearchMaterial);
            SetupStageMaterial(gpuFinalizeMaterial);
            compressionStartedAt = Time.realtimeSinceStartup;
            // 16候補を2候補ずつ評価し、batchごとの最良結果を小さいping-pong RTへ統合する
            // 候補数と探索内容は従来のままにし、1 frameへ集中するGPU処理と作業RT幅だけを減らす

            // 同期ReadPixelsはVRChat実行中にstallしやすいため、VRCAsyncGPUReadbackで完了callbackを待つ
            compressionPending = true;
            SetStatus(isWarmup ? "BC7 shader warmup started." : "BC7 GPU compression started.");
            SendCustomEventDelayedFrames(nameof(_RunGpuCompressionPass), 1);
        }

        // 圧縮passで共通して使うMaterial parameterを設定する
        private void SetupStageMaterial(Material material)
        {
            material.SetFloat("_SourceWidth", sourceWidth);
            material.SetFloat("_SourceHeight", sourceHeight);
            material.SetFloat("_OutputWidth", blockCountX * 4);
            material.SetFloat("_OutputHeight", blockCountY);
            material.SetFloat("_EncodeSrgb", activeEncodeSrgb ? 1f : 0f);
            material.SetFloat("_SourceTextureSrgb", activeSourceSrgb ? 1f : 0f);
            material.SetFloat("_CandidateOutputWidth", blockCountX * CandidateEndpointPixelsPerBlock);
            material.SetFloat("_BestOutputWidth", blockCountX * BestCandidatePixelsPerBlock);
            material.SetTexture("_SourceTex", sourceTexture);
        }

        // sourceとdestinationには必ず別Textureを渡す。同一RTへのBlitは結果が未定義になり、
        // PCでの並列実行時は別library instanceの作業RTとも共有しない。Material値は各Blit直前に設定する
        private void TrackedBlit(Texture source, RenderTexture destination)
        {
            VRCGraphics.Blit(source, destination);
            TrackGpuBlitDestination(destination);
        }

        // 指定MaterialとpassのBlitを投入してGPU処理対象を記録する
        private void TrackedBlit(Texture source, RenderTexture destination, Material material, int pass)
        {
            VRCGraphics.Blit(source, destination, material, pass);
            TrackGpuBlitDestination(destination);
        }

        // 直近のBlit出力と未確認batch数を記録する
        private void TrackGpuBlitDestination(RenderTexture destination)
        {
            if ((!compressionPending && !warmupRunning && !inputCopyPending) || destination == null)
            {
                return;
            }
            // 同じcallback内の複数Blitは1 batchとして扱い、最後の出力だけをfence sourceにする
            gpuBlitSubmittedSinceSchedule = true;
            gpuFenceSourceTexture = destination;
        }

        // GPU batch完了後に呼ぶ継続イベントを予約する
        private void ScheduleAfterGpuBatch(string eventName)
        {
            // Blitごとのreadbackは総時間を大きく増やすため、GPU命令は安全な範囲でbatch化する
            // readback開始・warmup完了・一時RT再利用の境界だけは、最後の出力をsampleするmarkerと
            // 1pixel readbackで完了を保証する。境界を外すと未完了のRTを上書き・破棄する可能性がある
            bool submittedBatch = gpuBlitSubmittedSinceSchedule && gpuFenceSourceTexture != null;
            int pendingBatchCount = gpuUnfencedBatchCount + (submittedBatch ? 1 : 0);
            RenderTexture fenceSource = submittedBatch
                ? gpuFenceSourceTexture
                : gpuUnfencedBatchSourceTexture;
            bool requiresFence = fenceSource != null && pendingBatchCount > 0
                && (pendingBatchCount >= GpuFenceBatchLimit || RequiresGpuFenceBeforeContinuation(eventName));
            CancelGpuFence();
            if (!requiresFence || (!compressionPending && !warmupRunning && !inputCopyPending))
            {
                gpuUnfencedBatchCount = pendingBatchCount;
                gpuUnfencedBatchSourceTexture = fenceSource;
                SendCustomEventDelayedFrames(eventName, 1);
                return;
            }

            gpuFenceSourceTexture = fenceSource;
            gpuFenceContinuationEvent = eventName;
            gpuFenceMarkerQueued = true;
            SendCustomEventDelayedFrames(nameof(_RunGpuFenceMarkerBlit), 1);
        }

        // 継続前にGPU完了確認が必要かを判定する
        private bool RequiresGpuFenceBeforeContinuation(string eventName)
        {
            return eventName == nameof(_CompleteInputCopy)
                || eventName == nameof(_StartCompressionWarmup)
                || eventName == nameof(_CompleteWarmup)
                || eventName == nameof(_BeginGpuReadback);
        }

        // 直近のGPU出力から完了確認用markerを生成する
        public void _RunGpuFenceMarkerBlit()
        {
            if ((!compressionPending && !warmupRunning && !inputCopyPending && !cancelledReadbackPending)
                || !gpuFenceMarkerQueued || gpuFenceSourceTexture == null)
            {
                return;
            }

            gpuFenceMarkerQueued = false;
            if (gpuFenceMarkerTexture == null)
            {
                gpuFenceMarkerTexture = new RenderTexture(
                    GpuFenceMarkerSize,
                    GpuFenceMarkerSize,
                    0,
                    RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.Linear);
                gpuFenceMarkerTexture.name = "IC_BC7_GpuFenceMarker";
                ConfigureRenderTexture(gpuFenceMarkerTexture);
            }
            if (!gpuFenceMarkerTexture.IsCreated())
            {
                FailActiveOperation("GPU fence marker texture creation failed.");
                return;
            }

            VRCGraphics.Blit(gpuFenceSourceTexture, gpuFenceMarkerTexture);
            gpuFenceReadbackQueued = true;
            SendCustomEventDelayedFrames(nameof(_RunGpuFenceReadbackRequest), 1);
        }

        // 完了確認用markerの非同期readbackを要求する
        public void _RunGpuFenceReadbackRequest()
        {
            if ((!compressionPending && !warmupRunning && !inputCopyPending && !cancelledReadbackPending)
                || !gpuFenceReadbackQueued || gpuFenceReadbackPending || gpuFenceMarkerTexture == null)
            {
                return;
            }

            gpuFenceReadbackQueued = false;
            gpuFenceReadbackRequest = VRCAsyncGPUReadback.Request(
                gpuFenceMarkerTexture,
                0,
                0, 1,
                0, 1,
                0, 1,
                TextureFormat.RGBA32,
                this);
            gpuFenceReadbackPending = true;
            SendCustomEventDelayedFrames(nameof(_PollGpuFenceReadback), 1);
        }

        // GPU完了確認用readbackを監視し継続イベントへ進む
        public void _PollGpuFenceReadback()
        {
            if ((!compressionPending && !warmupRunning && !inputCopyPending && !cancelledReadbackPending) || !gpuFenceReadbackPending)
            {
                return;
            }
            if (!gpuFenceReadbackRequest.done)
            {
                SendCustomEventDelayedFrames(nameof(_PollGpuFenceReadback), 1);
                return;
            }

            gpuFenceReadbackPending = false;
            if (cancelledReadbackPending)
            {
                _PollCancelledReadback();
                return;
            }
            if (gpuFenceReadbackRequest.hasError)
            {
                CancelGpuFence();
                FailActiveOperation("GPU fence readback failed.");
                return;
            }

            string continuationEvent = gpuFenceContinuationEvent;
            CancelGpuFence();
            if (string.IsNullOrEmpty(continuationEvent))
            {
                FailActiveOperation("GPU fence continuation is missing.");
                return;
            }
            SendCustomEventDelayedFrames(continuationEvent, 1);
        }

        // GPU完了確認の予約状態と参照を初期化する
        private void CancelGpuFence()
        {
            gpuBlitSubmittedSinceSchedule = false;
            gpuFenceSourceTexture = null;
            gpuUnfencedBatchSourceTexture = null;
            gpuUnfencedBatchCount = 0;
            gpuFenceMarkerQueued = false;
            gpuFenceReadbackQueued = false;
            gpuFenceReadbackPending = false;
            gpuFenceContinuationEvent = "";
        }

        // 未完了readbackを待って圧縮用GPUリソースを一括解放する
        private void ReleaseGpuResourcesWhenReadbacksComplete()
        {
            bool mainReadbackWaiting = gpuReadbackStarted && !gpuReadbackRequest.done;
            if (!mainReadbackWaiting
                && !gpuFenceMarkerQueued
                && !gpuFenceReadbackQueued
                && !gpuFenceReadbackPending)
            {
                RenderTexture pendingFenceSource = gpuBlitSubmittedSinceSchedule && gpuFenceSourceTexture != null
                    ? gpuFenceSourceTexture
                    : gpuUnfencedBatchSourceTexture;
                if (pendingFenceSource != null && (gpuBlitSubmittedSinceSchedule || gpuUnfencedBatchCount > 0))
                {
                    gpuFenceSourceTexture = pendingFenceSource;
                    gpuBlitSubmittedSinceSchedule = false;
                    gpuUnfencedBatchSourceTexture = null;
                    gpuUnfencedBatchCount = 0;
                    gpuFenceMarkerQueued = true;
                }
            }
            if (gpuFenceMarkerQueued && gpuFenceSourceTexture == null)
            {
                gpuFenceMarkerQueued = false;
            }

            bool fenceReadbackWaiting = gpuFenceReadbackPending && !gpuFenceReadbackRequest.done;
            if (gpuFenceMarkerQueued || gpuFenceReadbackQueued || mainReadbackWaiting || fenceReadbackWaiting)
            {
                cancelledReadbackPending = true;
                if (mainReadbackWaiting)
                {
                    SendCustomEventDelayedFrames(nameof(_PollCancelledReadback), 1);
                }
                else if (gpuFenceMarkerQueued)
                {
                    SendCustomEventDelayedFrames(nameof(_RunGpuFenceMarkerBlit), 1);
                }
                else if (gpuFenceReadbackQueued)
                {
                    SendCustomEventDelayedFrames(nameof(_RunGpuFenceReadbackRequest), 1);
                }
                else
                {
                    SendCustomEventDelayedFrames(nameof(_PollCancelledReadback), 1);
                }
                return;
            }

            cancelledReadbackPending = false;
            gpuReadbackStarted = false;
            gpuReadbackQueued = false;
            CancelGpuFence();
            _ReleaseGpuResources();
            FlushTerminalNotifications();
        }

        // キャンセル後のreadback完了を監視してGPUリソースを解放する
        public void _PollCancelledReadback()
        {
            if (!cancelledReadbackPending)
            {
                return;
            }

            ReleaseGpuResourcesWhenReadbacksComplete();
        }


        // Udon eventをframeごとに送り直し、各呼び出しでは1回のBlitだけを実行する
        // batchごとの最良候補は小さいping-pong RTへ統合し、最終batch後だけbyte搬送面へpackする
        public void _RunGpuCompressionPass()
        {
            if (!compressionPending || gpuCompressMaterial == null || gpuRefineMaterial == null
                || gpuScaleSearchMaterial == null || gpuNudgeSearchMaterial == null
                || gpuFinalizeMaterial == null)
            {
                return;
            }

            if (gpuPassStep == 0)
            {
                gpuCompressMaterial.SetFloat("_CandidateBatchOffset", gpuCandidateBatchIndex * CandidateBatchSize);
                TrackedBlit(sourceTexture, gpuCandidateTexture, gpuCompressMaterial, CandidateEndpointsBatch0Pass + gpuCandidateBatchIndex);
                gpuCompressMaterial.SetTexture("_CandidateTex", gpuCandidateTexture);
            }
            else if (gpuPassStep == 1)
            {
                gpuRefineMaterial.SetTexture("_CandidateTex", gpuCandidateTexture);
                TrackedBlit(gpuCandidateTexture, gpuCandidateScratchTexture, gpuRefineMaterial, LeastSquaresPass);
            }
            else if (gpuPassStep == 2)
            {
                gpuRefineMaterial.SetTexture("_CandidateTex", gpuCandidateScratchTexture);
                TrackedBlit(gpuCandidateScratchTexture, gpuCandidateTexture, gpuRefineMaterial, LeastSquaresPass);
            }
            else if (gpuPassStep == 3)
            {
                gpuScaleSearchMaterial.SetTexture("_CandidateTex", gpuCandidateTexture);
                TrackedBlit(gpuCandidateTexture, gpuCandidateScratchTexture, gpuScaleSearchMaterial, ScaleSearchPass);
            }
            else if (gpuPassStep == 4)
            {
                gpuNudgeSearchMaterial.SetTexture("_BaseCandidateTex", gpuCandidateTexture);
                gpuNudgeSearchMaterial.SetTexture("_CandidateTex", gpuCandidateScratchTexture);
                TrackedBlit(gpuCandidateScratchTexture, gpuCandidateFinalTexture, gpuNudgeSearchMaterial, NudgeSearchPass);
            }
            else if (gpuPassStep == 5)
            {
                RenderTexture previousBest = GetCurrentBestCandidateTexture();
                RenderTexture nextBest = GetNextBestCandidateTexture();
                gpuFinalizeMaterial.SetTexture("_CandidateTex", gpuCandidateFinalTexture);
                gpuFinalizeMaterial.SetFloat("_HasPreviousBest", gpuCandidateBatchIndex > 0 ? 1f : 0f);
                gpuFinalizeMaterial.SetTexture("_PreviousBestCandidateTex", previousBest);
                TrackedBlit(gpuCandidateFinalTexture, nextBest, gpuFinalizeMaterial, BestCandidatePass);
                gpuCandidateBatchIndex++;
                if (gpuCandidateBatchIndex < CandidateBatchCount)
                {
                    gpuPassStep = 0;
                    ScheduleAfterGpuBatch(nameof(_RunGpuCompressionPass));
                    return;
                }
                gpuFinalizeMaterial.SetTexture("_BestCandidateTex", nextBest);
            }
            else if (gpuPassStep == 6)
            {
                TrackedBlit(GetCurrentBestCandidateTexture(), gpuOutputTexture, gpuFinalizeMaterial, PackBytesPass);
            }

            gpuPassStep++;
            if (gpuPassStep < 7)
            {
                ScheduleAfterGpuBatch(nameof(_RunGpuCompressionPass));
                return;
            }

            if (activeRunIsWarmup)
            {
                ScheduleAfterGpuBatch(nameof(_CompleteWarmup));
                return;
            }
            gpuReadbackQueued = true;
            SetStatus("BC7 GPU compression readback queued.");
            ScheduleAfterGpuBatch(nameof(_BeginGpuReadback));
        }

        // 現在の最良候補を保持するRenderTextureを返す
        private RenderTexture GetCurrentBestCandidateTexture()
        {
            if (gpuCandidateBatchIndex <= 0)
            {
                return null;
            }
            return (gpuCandidateBatchIndex & 1) == 0 ? gpuBestCandidateScratchTexture : gpuBestCandidateTexture;
        }

        // 次の最良候補を書き込むRenderTextureを返す
        private RenderTexture GetNextBestCandidateTexture()
        {
            return (gpuCandidateBatchIndex & 1) == 0 ? gpuBestCandidateTexture : gpuBestCandidateScratchTexture;
        }

        // 圧縮済みbyte搬送面の非同期readbackを開始する
        public void _BeginGpuReadback()
        {
            if (!compressionPending || activeRunIsWarmup || gpuOutputTexture == null
                || gpuReadbackStarted || gpuPassStep < 7 || !gpuReadbackQueued)
            {
                gpuReadbackQueued = false;
                return;
            }

            gpuReadbackQueued = false;
            gpuReadbackRequest = VRCAsyncGPUReadback.Request(gpuOutputTexture, 0, TextureFormat.RGBA32, this);
            gpuReadbackStarted = true;
            SendCustomEventDelayedFrames(nameof(_PollGpuReadback), 1);
        }

        // 保存中requestをpollするためcallbackでは振り分けない。古いrequestが次runへ届く競合を避ける
        public override void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest completedRequest)
        {
        }

        // 圧縮結果のGPU readback完了とerrorを確認する
        public void _PollGpuReadback()
        {
            if ((!compressionPending && !cancelledReadbackPending) || !gpuReadbackStarted)
            {
                return;
            }
            if (!gpuReadbackRequest.done)
            {
                SendCustomEventDelayedFrames(nameof(_PollGpuReadback), 1);
                return;
            }

            if (cancelledReadbackPending)
            {
                _PollCancelledReadback();
                return;
            }

            gpuReadbackStarted = false;
            gpuReadbackQueued = false;
            readbackElapsedMs = GetElapsedMs(compressionStartedAt);
            FinishGpuReadback();
        }

        // GPUのRGBA32搬送面をnative BC7 byte[]へ取り出す。CPU側でbyte順の変換や再圧縮は行わない
        private void FinishGpuReadback()
        {
            compressionPending = false;
            if (gpuReadbackRequest.hasError)
            {
                FailCompression("VRCAsyncGPUReadback request hasError.");
                return;
            }

            int expectedBytes = GetCompressedByteCount(sourceWidth, sourceHeight);
            compressedByteCount = expectedBytes;
            // TryGetDataの配列長はGPU出力と完全一致させる。余分なmetadataやheaderは付与しない
            byte[] bytes = new byte[expectedBytes];
            if (!gpuReadbackRequest.TryGetData(bytes, 0))
            {
                FailCompression("TryGetData failed for BC7 GPU byte[] readback.");
                return;
            }

            // GPUから取得したnative BC7 byte列を圧縮結果として公開する
            compressedBytes = bytes;
            // プレビューが不要な利用ではTextureを生成せず、byte[]だけを返してVRAMと生成時間を抑える
            if (autoCreateCompressedTexture)
            {
                _CreateCompressedTextureFromOutput();
                if (compressionFailed)
                {
                    return;
                }

                if (clearCompressedBytesAfterTextureCreated)
                {
                    compressedBytes = null;
                }
            }

            ReleaseSourceTextureIfNeeded();
            _ReleaseGpuResources();
            compressionComplete = true;
            totalElapsedMs = GetElapsedMs(compressionStartedAt);
            SetStatus("BC7 GPU compression completed. bytes=" + expectedBytes.ToString()
                + " sRGB=" + (activeEncodeSrgb ? "1" : "0")
                + " readbackMs=" + ((int)readbackElapsedMs).ToString()
                + " totalMs=" + ((int)totalElapsedMs).ToString());
            SendCompletionEvent();
        }

        // 任意のプレビュー/検証用。Unityへnative BC7 byte[]をそのまま渡すため、ここでbyte順を変更してはいけない
        public void _CreateCompressedTextureFromOutput()
        {
            byte[] outputBytes = compressedBytes;

            if (outputBytes == null || sourceWidth <= 0 || sourceHeight <= 0)
            {
                FailCompression("Cannot create BC7 texture because output bytes or size are invalid.");
                return;
            }

            int expectedBytes = GetCompressedByteCount(sourceWidth, sourceHeight);
            if (outputBytes.Length != expectedBytes)
            {
                FailCompression("BC7 byte array size does not match expected size.");
                return;
            }

            _ClearCompressedTexture();
            // raw BC7 payloadには元画像寸法が含まれないため、ここで使う寸法は圧縮時と完全一致が必要
            // 4x4未満や端数寸法はblock数を切り上げられるが、TextureFormat対応と最大寸法は実行端末に依存する
            // BC7 resource自身のGraphicsFormatで格納値の色領域を指定する
            // sRGB値を格納したbyte列はlinear:false、linear値はlinear:trueで生成する
            // これはsampling時の解釈指定であり、ARGB32への色空間復元Blitは行わない
            Texture2D texture = new Texture2D(sourceWidth, sourceHeight, TextureFormat.BC7, false, !activeEncodeSrgb);
            texture.LoadRawTextureData(outputBytes);
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.Apply(false, false);

            compressedTexture = texture;
            SetStatus("BC7 texture created. bytes=" + expectedBytes.ToString()
                + " encodedSrgb=" + (activeEncodeSrgb ? "1" : "0"));
        }

        // 画像端の端数pixelも4x4 blockへ切り上げる。encoder/expander/LoadRawTextureDataで必ず同じ計算を使う
        public int GetCompressedByteCount(int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                return 0;
            }

            long byteCount = (long)GetBlockCountX(width) * (long)GetBlockCountY(height) * BytesPerBlock;
            return byteCount > 0L && byteCount <= int.MaxValue ? (int)byteCount : 0;
        }

        // 必要寸法のBC7 byte搬送用RenderTextureを確保する
        private bool EnsureGpuOutputTexture(int width, int height)
        {
            // 1 blockを横4pixel（RGBA×4 = 16byte）へ配置し、縦方向はblock行数に対応させる
            int outputWidth = GetBlockCountX(width) * 4;
            int outputHeight = GetBlockCountY(height);
            if (gpuOutputTexture != null
                && gpuOutputTexture.width == outputWidth
                && gpuOutputTexture.height == outputHeight
                && gpuOutputTexture.format == RenderTextureFormat.ARGB32)
            {
                gpuOutputTexture.filterMode = FilterMode.Point;
                gpuOutputTexture.wrapMode = TextureWrapMode.Clamp;
                if (!gpuOutputTexture.IsCreated())
                {
                    gpuOutputTexture.Create();
                }

                return gpuOutputTexture.IsCreated();
            }

            if (!autoCreateGpuOutputTexture)
            {
                return false;
            }

            _ClearAutoCreatedGpuOutputTexture();
            gpuOutputTexture = new RenderTexture(outputWidth, outputHeight, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            gpuOutputTextureWasAutoCreated = true;
            gpuOutputTexture.filterMode = FilterMode.Point;
            gpuOutputTexture.wrapMode = TextureWrapMode.Clamp;
            gpuOutputTexture.Create();
            return gpuOutputTexture.IsCreated();
        }

        // 圧縮候補探索に必要な作業用RenderTextureを確保する
        private bool EnsureGpuWorkingTextures(int width, int height)
        {
            int blockWidth = GetBlockCountX(width);
            int blockHeight = GetBlockCountY(height);
            int candidateWidth = blockWidth * CandidateEndpointPixelsPerBlock;
            int bestWidth = blockWidth * BestCandidatePixelsPerBlock;

            // endpointはARGBHalf、Rだけを使う誤差面はRFloatにして精度を保ったまま一時VRAMを抑える
            bool candidateReady = EnsureRenderTexture(ref gpuCandidateTexture, candidateWidth, blockHeight, RenderTextureFormat.ARGBHalf);
            bool candidateScratchReady = EnsureRenderTexture(ref gpuCandidateScratchTexture, candidateWidth, blockHeight, RenderTextureFormat.ARGBHalf);
            bool candidateFinalReady = EnsureRenderTexture(ref gpuCandidateFinalTexture, candidateWidth, blockHeight, RenderTextureFormat.ARGBHalf);
            bool bestReady = EnsureRenderTexture(ref gpuBestCandidateTexture, bestWidth, blockHeight, RenderTextureFormat.ARGBHalf);
            bool bestScratchReady = EnsureRenderTexture(ref gpuBestCandidateScratchTexture, bestWidth, blockHeight, RenderTextureFormat.ARGBHalf);
            if (candidateReady && candidateScratchReady && candidateFinalReady && bestReady && bestScratchReady)
            {
                return true;
            }

            if (!autoCreateGpuWorkingTextures)
            {
                return false;
            }

            if (!candidateReady)
            {
                _ClearAutoCreatedGpuCandidateTexture();
                gpuCandidateTexture = new RenderTexture(candidateWidth, blockHeight, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                gpuCandidateTextureWasAutoCreated = true;
                ConfigureRenderTexture(gpuCandidateTexture);
            }

            if (!candidateScratchReady)
            {
                _ClearAutoCreatedGpuCandidateScratchTexture();
                gpuCandidateScratchTexture = new RenderTexture(candidateWidth, blockHeight, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                gpuCandidateScratchTextureWasAutoCreated = true;
                ConfigureRenderTexture(gpuCandidateScratchTexture);
            }

            if (!candidateFinalReady)
            {
                _ClearAutoCreatedGpuCandidateFinalTexture();
                gpuCandidateFinalTexture = new RenderTexture(candidateWidth, blockHeight, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                gpuCandidateFinalTextureWasAutoCreated = true;
                ConfigureRenderTexture(gpuCandidateFinalTexture);
            }

            if (!bestReady)
            {
                _ClearAutoCreatedGpuBestCandidateTexture();
                gpuBestCandidateTexture = new RenderTexture(bestWidth, blockHeight, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                gpuBestCandidateTextureWasAutoCreated = true;
                ConfigureRenderTexture(gpuBestCandidateTexture);
            }

            if (!bestScratchReady)
            {
                _ClearAutoCreatedGpuBestCandidateScratchTexture();
                gpuBestCandidateScratchTexture = new RenderTexture(bestWidth, blockHeight, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                gpuBestCandidateScratchTextureWasAutoCreated = true;
                ConfigureRenderTexture(gpuBestCandidateScratchTexture);
            }

            return gpuCandidateTexture != null && gpuCandidateTexture.IsCreated()
                && gpuCandidateScratchTexture != null && gpuCandidateScratchTexture.IsCreated()
                && gpuCandidateFinalTexture != null && gpuCandidateFinalTexture.IsCreated()
                && gpuBestCandidateTexture != null && gpuBestCandidateTexture.IsCreated()
                && gpuBestCandidateScratchTexture != null && gpuBestCandidateScratchTexture.IsCreated();
        }

        // 指定条件に一致するRenderTextureを確保する
        private bool EnsureRenderTexture(ref RenderTexture texture, int width, int height, RenderTextureFormat format)
        {
            // サイズまたはformatが違う既存Textureを再利用するとpass間のpixel addressingが崩れるため厳密に一致確認する
            if (texture == null || texture.width != width || texture.height != height || texture.format != format)
            {
                return false;
            }

            ConfigureRenderTexture(texture);
            return texture.IsCreated();
        }

        // 作業用RenderTextureのsampling設定を固定する
        private void ConfigureRenderTexture(RenderTexture texture)
        {
            if (texture == null)
            {
                return;
            }

            // 作業Textureはbyte/候補の配列として扱うため、補間とRepeatによる隣接要素の混入を禁止する
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;
            if (!texture.IsCreated())
            {
                texture.Create();
            }
        }

        // 画像幅から横方向のBC7ブロック数を求める
        private int GetBlockCountX(int width)
        {
            // 整数除算でceil(width / 4)を求める
            return (int)(((long)width + BlockWidth - 1L) / BlockWidth);
        }

        // 画像高さから縦方向のBC7ブロック数を求める
        private int GetBlockCountY(int height)
        {
            return (int)(((long)height + BlockHeight - 1L) / BlockHeight);
        }

        // 圧縮処理を失敗状態にしてGPUリソース解放と通知を行う
        private void FailCompression(string message)
        {
            inputCopyPending = false;
            compressionPending = false;
            compressionComplete = false;
            compressionFailed = true;
            if (compressionStartedAt > 0f)
            {
                totalElapsedMs = GetElapsedMs(compressionStartedAt);
            }
            SetStatus("Failed: " + message);
            // cleanup待機を先に確定し、失敗eventから同じライブラリオブジェクトが再入されないようにする
            ReleaseGpuResourcesWhenReadbacksComplete();
            SendFailedEvent();
        }

        // 実行中処理が圧縮かwarmupかを判定して失敗処理へ進める
        private void FailActiveOperation(string message)
        {
            if (!activeRunIsWarmup)
            {
                FailCompression(message);
                return;
            }

            compressionPending = false;
            warmupRunning = false;
            warmupComplete = false;
            warmupFailed = true;
            activeRunIsWarmup = false;
            RestoreSourceAfterWarmup();
            SetStatus("BC7 shader warmup failed: " + message);
            ReleaseGpuResourcesWhenReadbacksComplete();
            NotifyCompressionWarmupFailed();
        }

        // 圧縮warmupを成功状態にして入力とGPUリソースを復元する
        public void _CompleteWarmup()
        {
            if (!warmupRunning || !activeRunIsWarmup)
            {
                return;
            }
            compressionPending = false;
            warmupRunning = false;
            warmupComplete = true;
            warmupFailed = false;
            activeRunIsWarmup = false;
            CancelGpuFence();
            RestoreSourceAfterWarmup();
            _ReleaseGpuResources();
            SetStatus("BC7 shader warmup complete.");
            NotifyCompressionWarmupComplete();
        }

        // warmup前の入力Textureと色領域設定を復元する
        private void RestoreSourceAfterWarmup()
        {
            sourceTexture = sourceTextureBeforeWarmup;
            sourceTextureSrgb = sourceTextureSrgbBeforeWarmup;
            encodeSrgb = encodeSrgbBeforeWarmup;
            sourceTextureBeforeWarmup = null;
            // warmupSourceTextureは失敗時のGPU完了確認にも必要になるため、
            // 実際の解放は共通のGPU resource解放処理へまとめる
        }

        // 指定時刻から現在までの経過時間をmillisecondで返す
        private float GetElapsedMs(float startedAt)
        {
            // Time.realtimeSinceStartupを使い、timeScaleの影響を受けずGPU処理全体を計測する
            return Mathf.Max((Time.realtimeSinceStartup - startedAt) * 1000f, 0f);
        }

        // 外部参照用の状態messageを更新する
        private void SetStatus(string value)
        {
            status = value;
        }

        // 所有権を受け取った入力Textureを解放する
        private void _ReleaseSourceTexture()
        {
            // destroySourceTextureOnCompleteを使う場合、sourceTextureのownershipがこのcomponentへ移る点に注意する
            if (sourceTexture == null)
            {
                return;
            }

            Destroy(sourceTexture);
            sourceTexture = null;
        }

        // 指定handleの圧縮処理をキャンセルする
        public bool CancelCompression(int handleId)
        {
            if (handleId == InvalidHandleId || handleId != activeCompressionHandleId)
            {
                return false;
            }

            cancelledCompressionHandleId = handleId;
            _CancelActiveCompression();
            return true;
        }

        // 指定handleの圧縮状態を返す
        public int GetCompressionState(int handleId)
        {
            if (handleId == InvalidHandleId) return TaskStateUnknown;
            if (handleId == activeCompressionHandleId)
            {
                return compressionPending ? TaskStateRunning : TaskStatePending;
            }
            if (handleId == completedCompressionHandleId) return TaskStateSucceeded;
            if (handleId == failedCompressionHandleId) return TaskStateFailed;
            if (handleId == cancelledCompressionHandleId) return cancelledReadbackPending ? TaskStateCancelling : TaskStateCancelled;
            return TaskStateUnknown;
        }

        // 指定handleの圧縮進行段階を返す
        public int GetCompressionStage(int handleId)
        {
            if (handleId == completedCompressionHandleId) return ProgressStageComplete;
            if (handleId != activeCompressionHandleId) return ProgressStageUnknown;
            if (!compressionPending) return ProgressStagePending;
            if (gpuReadbackStarted) return ProgressStageReadback;
            return ProgressStageProcessing;
        }

        // 指定handleの圧縮進捗を0から1で返す
        public float GetCompressionProgress01(int handleId)
        {
            if (handleId == completedCompressionHandleId) return 1f;
            if (handleId != activeCompressionHandleId) return -1f;
            if (!compressionPending) return 0f;
            if (gpuReadbackStarted) return 0.9f;

            const int passesPerCandidateBatch = 6;
            int totalPassCount = CandidateBatchCount * passesPerCandidateBatch + 1;
            int completedPassCount = gpuCandidateBatchIndex * passesPerCandidateBatch;
            if (gpuCandidateBatchIndex < CandidateBatchCount)
            {
                completedPassCount += Mathf.Clamp(gpuPassStep, 0, passesPerCandidateBatch - 1);
            }
            else if (gpuPassStep > 6)
            {
                completedPassCount++;
            }
            return 0.05f + Mathf.Clamp01((float)completedPassCount / totalPassCount) * 0.8f;
        }

        // 指定handleの圧縮warmupをキャンセルする
        public bool CancelCompressionWarmup(int handleId)
        {
            if (handleId == InvalidHandleId || handleId != activeCompressionWarmupHandleId)
            {
                return false;
            }
            _CancelActiveCompression();
            return true;
        }

        // 指定handleの圧縮warmup状態を返す
        public int GetCompressionWarmupState(int handleId)
        {
            if (handleId == InvalidHandleId) return TaskStateUnknown;
            if (handleId == activeCompressionWarmupHandleId)
            {
                return warmupRunning ? TaskStateRunning : TaskStatePending;
            }
            if (handleId == completedCompressionWarmupHandleId) return TaskStateSucceeded;
            if (handleId == failedCompressionWarmupHandleId) return TaskStateFailed;
            if (handleId == cancelledCompressionWarmupHandleId) return TaskStateCancelled;
            return TaskStateUnknown;
        }

        // 指定handleの圧縮warmup進行段階を返す
        public int GetCompressionWarmupStage(int handleId)
        {
            if (handleId == completedCompressionWarmupHandleId) return ProgressStageComplete;
            if (handleId != activeCompressionWarmupHandleId) return ProgressStageUnknown;
            if (!warmupRunning || !compressionPending) return ProgressStagePending;
            return ProgressStageProcessing;
        }

        // 指定handleの圧縮warmup進捗を0から1で返す
        public float GetCompressionWarmupProgress01(int handleId)
        {
            if (handleId == completedCompressionWarmupHandleId) return 1f;
            if (handleId != activeCompressionWarmupHandleId) return -1f;
            if (!warmupRunning || !compressionPending) return 0f;

            const int passesPerCandidateBatch = 6;
            int totalPassCount = CandidateBatchCount * passesPerCandidateBatch + 1;
            int completedPassCount = gpuCandidateBatchIndex * passesPerCandidateBatch;
            if (gpuCandidateBatchIndex < CandidateBatchCount)
            {
                completedPassCount += Mathf.Clamp(gpuPassStep, 0, passesPerCandidateBatch - 1);
            }
            else if (gpuPassStep > 6)
            {
                completedPassCount++;
            }
            return 0.05f + Mathf.Clamp01((float)completedPassCount / totalPassCount) * 0.9f;
        }

        // 実行中の圧縮処理を停止して関連状態を初期化する
        public void _CancelActiveCompression()
        {
            failureNotificationPending = false;
            inputCopyPending = false;
            if (activeCompressionHandleId != InvalidHandleId)
            {
                cancelledCompressionHandleId = activeCompressionHandleId;
                cancellationReceiver = compressionRequestReceiver;
                cancellationNotificationPending = true;
            }
            bool cancellingWarmup = activeCompressionWarmupHandleId != InvalidHandleId;
            if (cancellingWarmup)
            {
                cancelledCompressionWarmupHandleId = activeCompressionWarmupHandleId;
                activeCompressionWarmupHandleId = InvalidHandleId;
                compressionWarmupRequestReceiver = null;
                compressionWarmupRequestedAt = 0f;
                warmupComplete = false;
                warmupFailed = false;
            }
            if (compressionPending)
            {
                compressionPending = false;
            }
            compressionStartedAt = 0f;
            warmupRunning = false;
            activeRunIsWarmup = false;
            RestoreSourceAfterWarmup();
            sourceTexture = null;
            compressedBytes = null;
            _ClearCompressedTexture();
            activeCompressionHandleId = InvalidHandleId;
            compressionRequestReceiver = null;
            SetStatus(cancellingWarmup
                ? "BC7 compression warmup cancelled."
                : "BC7 compression cancelled.");
            ReleaseGpuResourcesWhenReadbacksComplete();
        }

        // 圧縮結果の公開byte列参照を解除する
        public void _ClearCompressedBytes()
        {
            compressedBytes = null;
            compressedByteCount = 0;
        }

        // 確認用に生成したBC7 Textureを破棄する
        public void _ClearCompressedTexture()
        {
            // UnityEngine.ObjectはGCだけではGPU resourceを即時解放できないためDestroyを使用する
            if (compressedTexture != null)
            {
                Destroy(compressedTexture);
                compressedTexture = null;
            }
        }

        // 自動生成した候補Textureを解放する
        private void _ClearAutoCreatedGpuCandidateTexture()
        {
            if (!gpuCandidateTextureWasAutoCreated || gpuCandidateTexture == null)
            {
                return;
            }

            ReleaseRenderTexture(gpuCandidateTexture);
            gpuCandidateTexture = null;
            gpuCandidateTextureWasAutoCreated = false;
        }

        // 自動生成した候補scratch Textureを解放する
        private void _ClearAutoCreatedGpuCandidateScratchTexture()
        {
            if (!gpuCandidateScratchTextureWasAutoCreated || gpuCandidateScratchTexture == null)
            {
                return;
            }
            ReleaseRenderTexture(gpuCandidateScratchTexture);
            gpuCandidateScratchTexture = null;
            gpuCandidateScratchTextureWasAutoCreated = false;
        }

        // 自動生成した候補確定Textureを解放する
        private void _ClearAutoCreatedGpuCandidateFinalTexture()
        {
            if (!gpuCandidateFinalTextureWasAutoCreated || gpuCandidateFinalTexture == null)
            {
                return;
            }
            ReleaseRenderTexture(gpuCandidateFinalTexture);
            gpuCandidateFinalTexture = null;
            gpuCandidateFinalTextureWasAutoCreated = false;
        }

        // 自動生成した最良候補Textureを解放する
        private void _ClearAutoCreatedGpuBestCandidateTexture()
        {
            if (!gpuBestCandidateTextureWasAutoCreated || gpuBestCandidateTexture == null)
            {
                return;
            }

            ReleaseRenderTexture(gpuBestCandidateTexture);
            gpuBestCandidateTexture = null;
            gpuBestCandidateTextureWasAutoCreated = false;
        }

        // 自動生成した最良候補scratch Textureを解放する
        private void _ClearAutoCreatedGpuBestCandidateScratchTexture()
        {
            if (!gpuBestCandidateScratchTextureWasAutoCreated || gpuBestCandidateScratchTexture == null)
            {
                return;
            }

            ReleaseRenderTexture(gpuBestCandidateScratchTexture);
            gpuBestCandidateScratchTexture = null;
            gpuBestCandidateScratchTextureWasAutoCreated = false;
        }

        // 圧縮成功イベントを要求元または設定済み受信先へ送る
        private void SendCompletionEvent()
        {
            if (activeCompressionHandleId != InvalidHandleId)
            {
                completedCompressionHandleId = activeCompressionHandleId;
                activeCompressionHandleId = InvalidHandleId;
            }
            if (compressionRequestReceiver != null)
            {
                UdonBehaviour receiver = compressionRequestReceiver;
                compressionRequestReceiver = null;
                receiver.SendCustomEvent(CompressionSucceededEventName);
                return;
            }
            SendConfiguredEvent(completionEventName);
        }

        // 圧縮失敗イベントを要求元または設定済み受信先へ送る
        private void SendFailedEvent()
        {
            // 呼び側が通知直後に次の操作へ進めるよう、解放完了まで失敗通知を保留する
            if (cancelledReadbackPending) { failureNotificationPending = true; return; }
            failureNotificationPending = false;
            if (activeCompressionHandleId != InvalidHandleId)
            {
                failedCompressionHandleId = activeCompressionHandleId;
                activeCompressionHandleId = InvalidHandleId;
            }
            if (compressionRequestReceiver != null)
            {
                UdonBehaviour receiver = compressionRequestReceiver;
                compressionRequestReceiver = null;
                receiver.SendCustomEvent(CompressionFailedEventName);
                return;
            }
            SendConfiguredEvent(failedEventName);
        }

        // 圧縮warmup成功イベントを要求元へ送る
        private void NotifyCompressionWarmupComplete()
        {
            if (activeCompressionWarmupHandleId == InvalidHandleId)
            {
                return;
            }
            completedCompressionWarmupHandleId = activeCompressionWarmupHandleId;
            activeCompressionWarmupHandleId = InvalidHandleId;
            compressionWarmupRequestedAt = 0f;
            if (compressionWarmupRequestReceiver != null)
            {
                UdonBehaviour receiver = compressionWarmupRequestReceiver;
                compressionWarmupRequestReceiver = null;
                receiver.SendCustomEvent(CompressionWarmupSucceededEventName);
            }
        }

        // 圧縮warmupを失敗状態にして後処理と通知を行う
        private void FailCompressionWarmup(string message)
        {
            compressionPending = false;
            warmupRunning = false;
            warmupComplete = false;
            warmupFailed = true;
            activeRunIsWarmup = false;
            RestoreSourceAfterWarmup();
            SetStatus("BC7 shader warmup failed: " + message);
            ReleaseGpuResourcesWhenReadbacksComplete();
            NotifyCompressionWarmupFailed();
        }

        // 圧縮warmup失敗イベントを要求元へ送る
        private void NotifyCompressionWarmupFailed()
        {
            if (activeCompressionWarmupHandleId != InvalidHandleId)
            {
                failedCompressionWarmupHandleId = activeCompressionWarmupHandleId;
                activeCompressionWarmupHandleId = InvalidHandleId;
            }
            compressionWarmupRequestedAt = 0f;
            if (compressionWarmupRequestReceiver != null)
            {
                UdonBehaviour receiver = compressionWarmupRequestReceiver;
                compressionWarmupRequestReceiver = null;
                receiver.SendCustomEvent(CompressionWarmupFailedEventName);
            }
        }

        // 次の圧縮handle IDを生成する
        private int GenerateCompressionHandleId()
        {
            nextCompressionHandleId++;
            if (nextCompressionHandleId <= 0)
            {
                nextCompressionHandleId = 1;
            }
            return nextCompressionHandleId;
        }

        // 次の圧縮warmup handle IDを生成する
        private int GenerateCompressionWarmupHandleId()
        {
            nextCompressionWarmupHandleId++;
            if (nextCompressionWarmupHandleId <= 0)
            {
                nextCompressionWarmupHandleId = 1;
            }
            return nextCompressionWarmupHandleId;
        }

        // 設定済み受信先へ指定名のcustom eventを送る
        private void SendConfiguredEvent(string eventName)
        {
            // UdonSharpではdelegate/eventを使わず、UdonBehaviour.SendCustomEventで完了を通知する
            if (completionEventReceiver == null || eventName == null || eventName.Length == 0)
            {
                return;
            }

            completionEventReceiver.SendCustomEvent(eventName);
        }

        // 設定に応じて入力Textureを処理完了時に解放する
        private void ReleaseSourceTextureIfNeeded()
        {
            if (ownedInputTexture != null || borrowedCopySource != null || !destroySourceTextureOnComplete)
            {
                return;
            }

            _ReleaseSourceTexture();
        }

        // 設定に応じて自動生成した作業用RenderTextureを解放する
        private void ReleaseAutoCreatedGpuWorkingTexturesIfNeeded()
        {
            if (!releaseAutoCreatedGpuWorkingTexturesOnComplete)
            {
                return;
            }

            _ClearAutoCreatedGpuCandidateTexture();
            _ClearAutoCreatedGpuCandidateScratchTexture();
            _ClearAutoCreatedGpuCandidateFinalTexture();
            _ClearAutoCreatedGpuBestCandidateTexture();
            _ClearAutoCreatedGpuBestCandidateScratchTexture();
        }

        // 圧縮で確保したGPUリソースを一括解放する
        private void _ReleaseGpuResources()
        {
            CancelGpuFence();
            ReleaseRenderTexture(gpuFenceMarkerTexture);
            gpuFenceMarkerTexture = null;
            ReleaseRenderTexture(warmupSourceTexture);
            warmupSourceTexture = null;
            _ClearAutoCreatedGpuOutputTexture();
            _ClearAutoCreatedGpuCandidateTexture();
            _ClearAutoCreatedGpuCandidateScratchTexture();
            _ClearAutoCreatedGpuCandidateFinalTexture();
            _ClearAutoCreatedGpuBestCandidateTexture();
            _ClearAutoCreatedGpuBestCandidateScratchTexture();
            ReleaseOwnedInput();
            ClearStageMaterialTextures(gpuCompressMaterial);
            ClearStageMaterialTextures(gpuRefineMaterial);
            ClearStageMaterialTextures(gpuScaleSearchMaterial);
            ClearStageMaterialTextures(gpuNudgeSearchMaterial);
            ClearStageMaterialTextures(gpuFinalizeMaterial);
        }

        // Materialが保持する一時Texture参照を解除する
        private void ClearStageMaterialTextures(Material material)
        {
            if (material != null)
            {
                material.SetTexture("_SourceTex", null);
                material.SetTexture("_CandidateTex", null);
                material.SetTexture("_BaseCandidateTex", null);
                material.SetTexture("_BestCandidateTex", null);
                material.SetTexture("_PreviousBestCandidateTex", null);
            }
        }

        // 自動生成したBC7 byte搬送用RenderTextureを解放する
        private void _ClearAutoCreatedGpuOutputTexture()
        {
            if (!gpuOutputTextureWasAutoCreated || gpuOutputTexture == null)
            {
                return;
            }

            ReleaseRenderTexture(gpuOutputTexture);
            gpuOutputTexture = null;
            gpuOutputTextureWasAutoCreated = false;
        }

        // 指定RenderTextureをReleaseして破棄する
        private void ReleaseRenderTexture(RenderTexture texture)
        {
            if (texture == null)
            {
                return;
            }
            if (texture.IsCreated())
            {
                texture.Release();
            }
            Destroy(texture);
        }

        // ここへ来る時点でGPU使用は終了している。所有している入力だけを破棄する
        private void ReleaseOwnedInput()
        {
            Texture input = ownedInputTexture;
            ownedInputTexture = null;
            if (sourceTexture == input || sourceTexture == borrowedCopySource) sourceTexture = null;
            borrowedCopySource = null;
            if (input == null) return;
            Destroy(input);
        }

        private void FlushTerminalNotifications()
        {
            if (cancelledReadbackPending) return;
            if (failureNotificationPending) SendFailedEvent();
            if (cancellationNotificationPending)
            {
                cancellationNotificationPending = false;
                UdonBehaviour receiver = cancellationReceiver;
                cancellationReceiver = null;
                if (receiver != null) receiver.SendCustomEvent("_HandleBc7CompressCancelled");
            }
            if (disposalRequested) ReleaseOwnedMaterials();
        }

        // 使用終了時はDestroyの前に呼び、IsDisposedを待つ。待機中もcomponentは有効に保つ
        public void _Dispose()
        {
            if (disposalRequested) return;
            disposalRequested = true;
            _CancelActiveCompression();
            _ClearCompressedTexture();
            FlushTerminalNotifications();
        }

        // Renderer.materialsで複製したものだけを破棄し、元assetは変更しない
        private void ReleaseOwnedMaterials()
        {
            if (ownedRuntimeMaterials != null)
            {
                for (int i = 0; i < ownedRuntimeMaterials.Length; i++) Destroy(ownedRuntimeMaterials[i]);
                ownedRuntimeMaterials = null;
            }
            disposed = true;
        }

        private void OnDestroy()
        {
            _ReleaseGpuResources();
            _ClearCompressedTexture();
            ReleaseOwnedMaterials();
        }
    }
}
