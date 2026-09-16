# IC BC7 for VRC

バージョン: v1.1.0

VRChat Worldの実行時に画像をBC7形式へ圧縮し、表示中のGPUメモリ使用量を削減するライブラリです。

作者: hy
ライセンス: CC0-1.0

## 動作要件

- Unity 2022.3.22f1
- VRChat Worlds SDK 3.10.4（このプロジェクトの開発環境）
- UdonSharp
- 圧縮には、使用Shader（最大target 3.5）、ARGBHalf / ARGB32 RenderTexture、GPU readbackを使用できる実行環境が必要です。
- BC7形式のTextureの表示にはBC7を扱えるGPU・graphics APIが必要です。圧縮byte列を生成する処理はShaderで行います。

## 入力画像のサイズ制限

- 圧縮・展開ともに、幅・高さはそれぞれ4以上かつ4の倍数である必要があります。UnityのBC7 Textureの寸法制限に合わせています。
- 2の累乗や正方形である必要はありません。例えば320×192、100×68は使用でき、513×257、1×1は使用できません。
- 条件を満たさない画像は自動リサイズせず、処理開始時にエラーログを出力して失敗状態にします。受付済みの要求は既存の失敗イベントで通知します。プレビュー生成にも同じ制限を適用します。

## 概要

- 4x4 texelごとに16 bytesのBC7ブロックを生成します。
- 主にPC向けRuntime pathを想定しています。Android・QuestではASTCを推奨します。
- encoderはalphaを保持できるmode 6 RGBAのみを生成します。
- payloadはBC7のnative byte列で、追加metadataはありません。
- RGBは指定に応じてsRGBまたはlinearの値として格納します。
- 直接展開入口・`RequestExpansion`ともにBC7形式のTextureを保持し、表示中のGPUメモリを削減します。

## フォルダ構成

- `Runtime/Prefabs`: Material参照を設定済みの`ICLibraryBc7.prefab`
- `Runtime/Scripts`: 圧縮・展開用UdonSharp component
- `Runtime/Shaders`: BC7 mode 6圧縮Shaderと共通処理
- `Runtime/Materials`: 各Shaderを使用するRuntime Material

## 簡単な利用ガイド

1. `Runtime/Prefabs/ICLibraryBc7.prefab`をSceneに配置し、利用側のUdonSharpスクリプトから`ICLibraryBc7`を参照します。

   圧縮要求はその`compression`へ送ります。
2. 初回の圧縮前に`RequestCompressionWarmup(eventReceiver)`を呼び、`_HandleBc7CompressWarmupComplete`の通知を待ちます。
3. `CanAcceptRequest`がtrueであることを確認し、入力Textureを渡します。

   CameraのRenderTextureなど共有する画像には`RequestCompressionCopy(...)`、処理後に破棄してよい専用Textureには`RequestCompressionOwned(...)`を使います。
4. 戻り値のhandleを保存します。

   `InvalidHandleId`なら受付失敗です。

   Copy入力は`IsInputCopyComplete(handle)`がtrueになるまで変更・解放せず、Owned入力は受付成功後に触らないでください。
5. `eventReceiver`で`_HandleBc7CompressComplete`を受けたら、`TakeCompressionResult(handle)`で圧縮byte列を取得します。

   失敗は`_HandleBc7CompressFailed`で受け取ります。

   `eventReceiver`には通知を処理するUdonBehaviourを指定してください。

要求時には入力と出力の色空間も指定します。結果のbyte列に画像寸法や色空間は含まれないため、幅・高さ・出力の色空間を一緒に保持してください。

## 実装ファイル

### C#

| ファイル | 役割 |
| --- | --- |
| `ICLibraryBc7.cs` | 圧縮用`Bc7CompressionLibrary`と展開用`Bc7ExpansionLibrary`の参照を保持します |
| `Bc7CompressionLibrary.cs` | 圧縮要求、入力検証、RenderTexture確保、Shader pass進行、GPU完了確認、readback、resource解放を管理します |
| `Bc7ExpansionLibrary.cs` | native BC7 byte列と画像寸法を検証し、`Texture2D`へuploadします。要求APIでも圧縮Textureを保持して結果を通知します |

### Shader

| ファイル | 役割 |
| --- | --- |
| `Bc7CompressCommon.cginc` | block読込、候補表現、誤差評価、mode 6 packingなどの共通処理です |
| `Bc7CompressBlit.shader` | endpoint候補を8 batchに分けて生成します |
| `Bc7RefineBlit.shader` | endpointを入力blockへ最小二乗で再適合させます |
| `Bc7ScaleSearchBlit.shader` | endpoint範囲を拡大・縮小して候補を探索します |
| `Bc7NudgeSearchBlit.shader` | endpointを小さく動かす局所探索を行います |
| `Bc7FinalizeBlit.shader` | batch間の最良候補を選択し、1 block 16 bytesへ格納します |

`Runtime/Materials`の各Materialは同名のShaderを参照し、`Bc7CompressionLibrary`がpassごとに入力Textureとparameterを設定して使用します。

## 圧縮フロー

1. `RequestCompressionOwned(...)`または`RequestCompressionCopy(...)`で圧縮を要求します。
2. `Bc7CompressionLibrary.cs`が入力Texture、画像寸法、必要byte数、Material参照を検証し、候補・作業・最終出力用RenderTextureを確保します。
3. `Bc7CompressBlit.shader`で8 batchのendpoint候補を順番に生成します。
4. 各batchを`Bc7RefineBlit.shader`で2回再適合し、`Bc7ScaleSearchBlit.shader`と`Bc7NudgeSearchBlit.shader`で探索します。
5. `Bc7FinalizeBlit.shader`の最良候補選択passで、現在の最良結果と比較します。
6. 全batchの処理後、同Shaderのpacking passがmode 6のnative BC7 byte列をRenderTextureへ書き込みます。
7. GPU完了確認後に`VRCAsyncGPUReadback`で`compressedBytes`へ取得します。
8. 処理中のRenderTextureはGPU完了確認後に解放し、必要な場合だけ`compressedTexture`を最終結果として保持します。

## 展開フロー

1. `RequestExpansion(...)`に圧縮byte列・幅・高さ・`encodedSrgb`・通知先を渡します。

   直接呼ぶ場合は`sourceBytes`・`sourceWidth`・`sourceHeight`・`encodedSrgb`を設定して`_LoadSourceBytesToTexture()`を呼びます。
2. 画像寸法から`ceil(width / 4) * ceil(height / 4) * 16`を求め、入力byte数の完全一致を確認します。
3. `TextureFormat.BC7`の`Texture2D`を生成し、`LoadRawTextureData`で圧縮byte列をそのまま読み込みます。
4. 圧縮形式のTextureを保持し、完了または失敗を通知します。

   要求APIの結果は`TakeExpansionResult(handle)`、直接呼んだ場合は`outputTexture`から取得します。

## 色空間とVRAM

### 圧縮する色空間（`outputSrgb`／`encodeSrgb`）

圧縮要求の`outputSrgb`は、画像をsRGBの色空間で圧縮するかを指定します。

内部の`encodeSrgb`に対応する設定です。

写真やイラストは`true`を基本とします。

sRGBで圧縮することで、暗い部分を含む色の階調を再現しやすくするための設定です。

展開時の`encodedSrgb`には、圧縮時の`outputSrgb`と**同じ値**を指定してください。

値が異なると、表示した画像の明るさや色が変わります。

この情報は圧縮byte列に含まれないため、画像の幅・高さと一緒に保持します。

Linearのマスク画像などでは`false`を使うことも考えられますが、この用途の画質は未確認です。本ライブラリは非可逆の画像圧縮を想定しています。

### 入力画像の色空間（`inputSrgb`）

`inputSrgb`は、圧縮へ渡すTextureのsRGB設定を指定します。

画像をImportしたTextureではInspectorの「sRGB (Color Texture)」、RenderTextureではそのTextureの`sRGB`設定に合わせます。

| UnityプロジェクトのColor Space | 入力TextureのsRGB設定 | `inputSrgb` |
| --- | --- | --- |
| Linear | 有効 | `true` |
| Linear | 無効 | `false` |
| Gamma | 有効 | `true` |
| Gamma | 無効 | `false` |

プロジェクト設定との組み合わせによる色変換は、ライブラリ内部で判定します。

LinearプロジェクトではUnityがsRGB Textureの読み取り時にlinearへ変換し、Gammaプロジェクトではその自動変換がないため、シェーダーが入力設定を参照します。

呼び出し側で「Linearプロジェクトだから`inputSrgb = false`」と置き換えないでください。

例えば、sRGBが無効のRenderTextureから写真を圧縮する場合は、`inputSrgb = false`、`outputSrgb = true`とし、展開時は`encodedSrgb = true`にします。

### 表示用TextureとGPUメモリ

`RequestExpansion(...)`は内部で`_LoadSourceBytesToTexture()`を呼び、BC7形式の`Texture2D`を生成します。

完了通知後に`TakeExpansionResult(handle)`で受け取り、Materialなどへ設定して表示します。

戻り値の型は`Texture`で、実体はBC7形式の`Texture2D`です。

`Texture2D`であることは、非圧縮RGBAであることを意味しません。

取得後の所有権は呼び出し側へ移り、使用後は`Destroy`してください。

`_LoadSourceBytesToTexture()`を直接呼ぶ場合も、`outputTexture`に同じ圧縮形式のTextureを生成します。

どちらの入口も、表示画像を非圧縮のRGBA／ARGB32 RenderTextureへ転写しません。

GPUが圧縮Textureを読み取って表示するため、表示中もGPUメモリの削減効果が保たれます。

ウォームアップでは、4×4のテストブロックを小さなARGB32 RenderTextureへ描画し、GPU処理の完了を確認します。

この一時処理は、利用側へ返す画像の形式や保持容量には影響しません。

## Runtime API

| 処理 | API・値 | 補足 |
| --- | --- | --- |
| 圧縮要求 | `RequestCompressionOwned(inputTexture, inputSrgb, outputSrgb, eventReceiver)` | handleを返し、次frameから処理します |
| 圧縮結果 | `compressedBytes` | native BC7 byte列です |
| 圧縮情報 | `sourceWidth`、`sourceHeight`、`compressedByteCount`、`blockCountX`、`blockCountY` | block数は端数を切り上げます |
| 圧縮状態 | `compressionPending`、`compressionComplete`、`compressionFailed`、`status` | handle APIでも状態を確認できます |
| Preview生成 | `_CreateCompressedTextureFromOutput()`、`compressedTexture` | `TextureFormat.BC7`です |
| 展開要求 | `RequestExpansion(inputBytes, width, height, encodedSrgb, eventReceiver)` | handleを返します |
| 展開開始 | `sourceBytes`、`sourceWidth`、`sourceHeight`、`_LoadSourceBytesToTexture()` | 入力byte数を検証します |
| 展開結果 | `TakeExpansionResult(handle)` | 圧縮形式のTextureを取得し、所有権を受け取ります |
| clear | `_ClearCompressedBytes()`、`_ClearSourceBytes()`、`_ClearOutputTexture()` | 保持結果を明示的に破棄します |

## 仕様対応状況

| 項目 | この実装 |
| --- | --- |
| block size | 4x4 texel |
| block容量 | 1 block 16 bytes |
| encoder mode | mode 6 RGBA、1 subset固定 |
| endpoint探索 | Runtime向けの候補生成、再適合、scale探索、局所探索 |
| native upload | `TextureFormat.BC7`と`LoadRawTextureData` |
| 対象platform | BC7対応GPU・driverを備えたPC向け |

## 入出力の所有権と終了処理

外部アプリからの新規利用は、handleを返す要求APIを使用してください。圧縮と展開はそれぞれのライブラリオブジェクトへ要求します。

| API | 所有権と利用方法 |
| --- | --- |
| `RequestCompressionOwned(...)` | 有効なhandleが返った時点で入力の所有権が移ります。以後、呼び側は入力を変更・再利用・Release・Destroyしません。成功、失敗、キャンセルのいずれでもライブラリがGPU完了後に破棄します |
| `RequestCompressionCopy(...)` | 引数はOwnedと同じです。共有asset・Cameraが再利用するRTなどに使います。元画像の所有権は呼び側に残り、内部コピーだけをライブラリが破棄します |
| `IsInputCopyComplete(handle)` | trueになれば元画像を変更・解放できます。コピーは非同期のため、呼び出し直後には変更・解放しないでください。コピー前の失敗・キャンセルでは終端状態または終了通知を待ちます |
| `TakeCompressionResult(handle)` | 成功したbyte[]を取り出し、ライブラリの保持参照を外します |
| `TakeExpansionResult(handle)` | 成功した圧縮Textureの所有権を呼び側へ渡します。取得後は次の要求やライブラリ終了で破棄されません。使用後のDestroyは呼び側が行います |
| `CanAcceptRequest` | trueのときだけ新しい要求を開始します。falseの間は呼び側のキューに残します |
| `CancelCompression(handle)` / `CancelExpansion(handle)` | キャンセルを受け付けます。GPU待機中は`TaskStateCancelling`、後処理完了後に`TaskStateCancelled`となります |
| `_Dispose()` / `IsDisposed` | ライブラリの使用を終了します。新しい要求を拒否し、未完了GPU処理を待ち、内部RT・未取得結果・複製Materialを解放します。`IsDisposed`を待ってライブラリのGameObjectをDestroyしてください |

- 受付に失敗して`InvalidHandleId`が返った場合、Ownedでも入力の所有権は移りません。
- Ownedへ渡せるのは、呼び側が破棄権限を持つ専用の実行時Textureです。

  Project asset、組み込みTexture、temporary RT poolから借りたRTは渡さず、Copyを使ってください。
- 圧縮入力のCopyは、元画像を取り込むためにARGB32の内部RTへ転写します。

  HDRや8bitを超える入力精度を保持するコピーではありません。

  転写時点の画像を取り込むため、取り込み完了まで元画像を固定します。
- 展開入力のbyte[]は処理終了まで書き換えないでください。参照の保持はライブラリが行います。
- 圧縮・ウォームアップでGPU処理を待っている場合、終了通知は必要な待機と後処理の後に届きます。キャンセル受理だけを解放完了として扱わないでください。
- 終了待機中はライブラリのcomponentとGameObjectを有効なまま保ちます。直接Destroy/無効化すると、Udonの後処理イベントを継続できません。
- 未取得の結果はライブラリが所有し、次の要求またはDisposeで破棄できます。継続利用する結果は必ずTakeで受け取ってください。

コピー完了イベントは`_HandleBc7InputCopied`、キャンセル完了は`_HandleBc7CompressCancelled` / `_HandleBc7ExpandCancelled`です。

## 導入方法

このフォルダを、VRChat Worlds SDKとUdonSharpを導入済みのUnityプロジェクト内の`Assets/HDAssets/ImageCompress/Bc7/`へ配置してください。  
各assetと対応する`.meta`は必ずセットで保持してください。  
導入後はUnityのimportとUdonSharpのコンパイルが完了するまで待ってから、PrefabをSceneへ配置してください。  
`Assets/SerializedUdonPrograms`は導入先で生成されるため、このリポジトリには含めません。

## リポジトリ

`https://github.com/hd4tails/ic-bc7-for-vrc.git`

## v1.1.0の変更

- 幅・高さが4未満または4の倍数ではない画像を、圧縮・展開・プレビュー生成の開始時に検出するようにしました。
- サイズ条件を満たさない場合はエラーログを出力して失敗状態にし、受付済みの要求は既存の失敗イベントで通知するようにしました。自動リサイズは行いません。
- 対応サイズの圧縮処理、byte列の形式と容量は変更していません。
