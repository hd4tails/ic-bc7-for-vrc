// BC7候補のエンドポイントを小さく動かし、局所探索で再現誤差を改善するシェーダー
// 構成: 1つのPassで構成し、Bc7CompressCommon.cgincを読み込んで共通の頂点処理・補助関数を利用する
Shader "HDAssets/IC/BC7/BC7NudgeSearchBlit"
{
    Properties
    {
        // 現在のBlitで基準入力として読むTexture
        _MainTex ("Source", 2D) = "white" {}
        // 圧縮候補の評価対象となる入力画像Texture
        _SourceTex ("Source Texture", 2D) = "white" {}
        // 現在評価している圧縮候補を保持するTexture
        _CandidateTex ("Candidate Texture", 2D) = "black" {}
        // 局所探索の基準となる圧縮候補を保持するTexture
        _BaseCandidateTex ("Base Candidate Texture", 2D) = "black" {}
        // 現在までの最良候補を保持するTexture
        _BestCandidateTex ("Best Candidate Texture", 2D) = "black" {}
        // 前段までの最良候補を保持するTexture
        _PreviousBestCandidateTex ("Previous Best Candidate Texture", 2D) = "black" {}
        // 入力幅
        _SourceWidth ("Source Width", Float) = 512
        // 入力高さ
        _SourceHeight ("Source Height", Float) = 512
        // 出力幅
        _OutputWidth ("Output Width", Float) = 512
        // 出力高さ
        _OutputHeight ("Output Height", Float) = 128
        // 候補Texture全体の幅
        _CandidateOutputWidth ("Candidate Output Width", Float) = 4096
        // 最良候補Textureの幅
        _BestOutputWidth ("Best Output Width", Float) = 768
        // 符号化するRGB値をsRGB領域として扱うかを示すフラグ
        _EncodeSrgb ("Encode sRGB", Float) = 1
        // 入力TextureがsRGB領域の値を保持しているかを示すフラグ
        _SourceTextureSrgb ("Source Texture sRGB", Float) = 0
        // このPassが担当する候補バッチの開始位置
        _CandidateBatchOffset ("Candidate Batch Offset", Float) = 0
        // 前段までの最良候補が利用可能かを示すフラグ
        _HasPreviousBest ("Has Previous Best", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Cull Off ZWrite Off ZTest Always
        CGINCLUDE
        #pragma target 3.5
        #include "Bc7CompressCommon.cginc"
        ENDCG
        Pass
        {
            Name "NudgeSearch"
            CGPROGRAM
            #pragma vertex vertMp
            #pragma fragment fragNudgeSearch
            ENDCG
        }
    }
    Fallback Off
}
