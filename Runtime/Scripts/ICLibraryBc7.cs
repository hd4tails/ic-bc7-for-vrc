using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace HDAssets.ImageCompress.Bc7
{
    /// <summary>
    /// BC7のライブラリの圧縮・展開のライブラリオブジェクトの参照を保持する
    /// BC7を使用するUdonにはこのオブジェクトを指定する
    /// 圧縮と展開の処理は参照先の各ライブラリオブジェクトが実行する
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class ICLibraryBc7 : UdonSharpBehaviour
    {
        [Header("Library Objects")]
        // BC7圧縮処理を実行するライブラリオブジェクト
        public Bc7CompressionLibrary compression;
        // BC7展開処理を実行するライブラリオブジェクト
        public Bc7ExpansionLibrary expansion;
    }
}
