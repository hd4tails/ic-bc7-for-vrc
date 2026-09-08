#include "UnityCG.cginc"

// 圧縮候補の評価対象となる入力画像Texture
sampler2D _SourceTex;
// 現在評価している圧縮候補を保持するTexture
sampler2D _CandidateTex;
// 局所探索の基準となる圧縮候補を保持するTexture
sampler2D _BaseCandidateTex;
// 現在までの最良候補を保持するTexture
sampler2D _BestCandidateTex;
// 前段までの最良候補を保持するTexture
sampler2D _PreviousBestCandidateTex;
// 入力幅
float _SourceWidth;
// 入力高さ
float _SourceHeight;
// 出力幅
float _OutputWidth;
// 出力高さ
float _OutputHeight;
// 候補Texture全体の幅
float _CandidateOutputWidth;
// 最良候補Textureの幅
float _BestOutputWidth;
// 符号化するRGB値をsRGB領域として扱うかを示すフラグ
float _EncodeSrgb;
// 入力TextureがsRGB領域の値を保持しているかを示すフラグ
float _SourceTextureSrgb;
// このPassが担当する候補バッチの開始位置
float _CandidateBatchOffset;
// 前段までの最良候補が利用可能かを示すフラグ
float _HasPreviousBest;

// 1つの候補生成Passで処理するBC7候補数
static const int BC7_CANDIDATE_BATCH_SIZE = 2;
// 1つの候補生成Passで処理するBC7候補数
static const int BC7_CANDIDATE_ENDPOINT_PIXELS = BC7_CANDIDATE_BATCH_SIZE * 2;
// 候補比較Textureで最良候補に割り当てるpixel数
static const int BC7_BEST_PIXELS = 3;

struct MpAppData
{
    // Object空間の入力頂点位置
    float4 vertex : POSITION;
    // 頂点処理で受け渡すUV座標
    float2 uv : TEXCOORD0;
};

struct MpVaryings
{
    // clip空間へ変換した頂点位置
    float4 vertex : SV_POSITION;
    // 頂点処理で受け渡すUV座標
    float2 uv : TEXCOORD0;
};

MpVaryings vertMp(MpAppData v)
{
    MpVaryings o;
    o.vertex = UnityObjectToClipPos(v.vertex);
    o.uv = v.uv;
    return o;
}

int2 OutputPixelMp(float2 uv, float width, float height)
{
    // BlitのUVを整数pixel座標へ戻し、端の丸め誤差で作業面外を参照しないようclampする
    int2 p = (int2)floor(uv * float2(width, height));
    p.x = clamp(p.x, 0, (int)width - 1);
    p.y = clamp(p.y, 0, (int)height - 1);
    return p;
}

float3 LinearToSrgbMp(float3 color)
{
    // sRGB payload用の区分関数。入力sampling領域と格納領域が異なる場合だけ使用する
    color = saturate(color);
    return lerp(color * 12.92, 1.055 * pow(color, 1.0 / 2.4) - 0.055, step(0.0031308, color));
}

float3 SrgbToLinearMp(float3 color)
{
    color = saturate(color);
    return lerp(color / 12.92, pow((color + 0.055) / 1.055, 2.4), step(0.04045, color));
}

bool SourceSampleIsSrgbMp()
{
    // Gamma/LinearプロジェクトでShaderへ渡るsampling値の領域差を吸収する
    #if defined(UNITY_COLORSPACE_GAMMA)
    return _SourceTextureSrgb > 0.5;
    #else
    return false;
    #endif
}

float3 SourceColorForBc7EncodingMp(float3 color)
{
    // endpointとpayloadを作るため、入力sampling値を指定されたBC7格納領域へ変換する
    bool sourceIsSrgb = SourceSampleIsSrgbMp();
    // 符号化するRGB値をsRGB領域として扱うかを示すフラグ
    bool encodeIsSrgb = _EncodeSrgb > 0.5;
    if (sourceIsSrgb == encodeIsSrgb)
    {
        return saturate(color);
    }

    return encodeIsSrgb ? LinearToSrgbMp(color) : SrgbToLinearMp(color);
}

float3 EncodedToSourceColorMp(float3 color)
{
    // 誤差評価ではdecode値を元のsampling領域へ戻し、表示時に近い領域で比較する
    bool sourceIsSrgb = SourceSampleIsSrgbMp();
    // 符号化するRGB値をsRGB領域として扱うかを示すフラグ
    bool encodeIsSrgb = _EncodeSrgb > 0.5;
    if (sourceIsSrgb == encodeIsSrgb)
    {
        return saturate(color);
    }

    return sourceIsSrgb ? LinearToSrgbMp(color) : SrgbToLinearMp(color);
}

float4 SourceLinearColorAtLocalMp(int blockX, int blockY, int localX, int localY)
{
    // 端数blockは最終texel複製で埋め、platformのwrap modeへ依存しない
    int srcX = min(blockX * 4 + localX, (int)_SourceWidth - 1);
    int srcY = min(blockY * 4 + localY, (int)_SourceHeight - 1);
    // 入力幅
    float2 uv = (float2(srcX, srcY) + 0.5) / float2(_SourceWidth, _SourceHeight);
    return saturate(tex2Dlod(_SourceTex, float4(uv, 0.0, 0.0)));
}

float4 SourceColorAtLocalMp(int blockX, int blockY, int localX, int localY)
{
    float4 color = SourceLinearColorAtLocalMp(blockX, blockY, localX, localY);
    color.rgb = SourceColorForBc7EncodingMp(color.rgb);

    return color;
}

float4 StorageToSourceColorMp(float4 color)
{
    // 格納値を入力Textureと同じ色領域へ戻す
    color.rgb = EncodedToSourceColorMp(color.rgb);

    return color;
}

float ComponentMp(float4 color, int channel)
{
    if (channel == 0) return color.r;
    if (channel == 1) return color.g;
    if (channel == 2) return color.b;
    return color.a;
}

int ColorByteMp(float value)
{
    return (int)round(saturate(value) * 255.0);
}

int EndpointByteMp(float4 endpoint, int channel)
{
    return ColorByteMp(ComponentMp(endpoint, channel));
}

void GetColorBoundsMp(int blockX, int blockY, out float4 minColor, out float4 maxColor)
{
    // 16texelのRGBA bounds。複数のendpoint候補で再利用する
    float4 firstColor = SourceColorAtLocalMp(blockX, blockY, 0, 0);
    minColor = firstColor;
    maxColor = firstColor;
    for (int y = 0; y < 4; y++)
    {
        for (int x = 0; x < 4; x++)
        {
            float4 color = SourceColorAtLocalMp(blockX, blockY, x, y);
            minColor = min(minColor, color);
            maxColor = max(maxColor, color);
        }
    }
}

float4 GetMeanColorMp(int blockX, int blockY)
{
    float4 total = 0.0;
    for (int y = 0; y < 4; y++)
    {
        for (int x = 0; x < 4; x++)
        {
            total += SourceColorAtLocalMp(blockX, blockY, x, y);
        }
    }

    return total * 0.0625;
}

float3 PrincipalRgbDirectionMp(int blockX, int blockY, float4 meanColor, float4 minColor, float4 maxColor)
{
    // RGB共分散行列と4回のpower iterationで主成分方向を近似する
    float c00 = 0.0;
    float c01 = 0.0;
    float c02 = 0.0;
    float c11 = 0.0;
    float c12 = 0.0;
    float c22 = 0.0;
    for (int y = 0; y < 4; y++)
    {
        for (int x = 0; x < 4; x++)
        {
            float3 delta = SourceColorAtLocalMp(blockX, blockY, x, y).rgb - meanColor.rgb;
            c00 += delta.r * delta.r;
            c01 += delta.r * delta.g;
            c02 += delta.r * delta.b;
            c11 += delta.g * delta.g;
            c12 += delta.g * delta.b;
            c22 += delta.b * delta.b;
        }
    }

    float3 direction = maxColor.rgb - minColor.rgb;
    if (dot(direction, direction) <= 0.000001)
    {
        direction = float3(1.0, 0.0, 0.0);
    }
    else
    {
        direction = normalize(direction);
    }

    for (int iteration = 0; iteration < 4; iteration++)
    {
        float3 nextDirection = float3(
            c00 * direction.r + c01 * direction.g + c02 * direction.b,
            c01 * direction.r + c11 * direction.g + c12 * direction.b,
            c02 * direction.r + c12 * direction.g + c22 * direction.b);
        float lengthSquared = dot(nextDirection, nextDirection);
        if (lengthSquared <= 0.000001)
        {
            return direction;
        }

        direction = nextDirection * rsqrt(lengthSquared);
    }

    return direction;
}

float ProjectWeightMp(float4 color, float4 endpoint0, float4 endpoint1)
{
    float4 direction = endpoint1 - endpoint0;
    float lengthSquared = dot(direction, direction);
    if (lengthSquared <= 0.000001)
    {
        return 0.0;
    }

    return saturate(dot(color - endpoint0, direction) / lengthSquared);
}

void GetOrderedEndpointsMp(int blockX, int blockY, out float4 endpoint0, out float4 endpoint1)
{
    // PCA近似軸へtexelを射影し、最小・最大位置の実色を候補にする
    float4 minColor;
    float4 maxColor;
    GetColorBoundsMp(blockX, blockY, minColor, maxColor);

    float4 meanColor = GetMeanColorMp(blockX, blockY);
    float3 direction = PrincipalRgbDirectionMp(blockX, blockY, meanColor, minColor, maxColor);
    if (dot(maxColor.rgb - minColor.rgb, maxColor.rgb - minColor.rgb) <= 0.000001)
    {
        endpoint0 = minColor;
        endpoint1 = maxColor;
        return;
    }

    endpoint0 = SourceColorAtLocalMp(blockX, blockY, 0, 0);
    endpoint1 = endpoint0;
    float minProjection = dot(endpoint0.rgb - meanColor.rgb, direction);
    float maxProjection = minProjection;
    for (int y = 0; y < 4; y++)
    {
        for (int x = 0; x < 4; x++)
        {
            float4 color = SourceColorAtLocalMp(blockX, blockY, x, y);
            float projection = dot(color.rgb - meanColor.rgb, direction);
            if (projection < minProjection)
            {
                endpoint0 = color;
                minProjection = projection;
            }
            if (projection > maxProjection)
            {
                endpoint1 = color;
                maxProjection = projection;
            }
        }
    }
}

void GetLumaEndpointsMp(int blockX, int blockY, out float4 endpoint0, out float4 endpoint1)
{
    // Rec.709輝度が最小・最大のtexelを候補にする
    endpoint0 = SourceColorAtLocalMp(blockX, blockY, 0, 0);
    endpoint1 = endpoint0;
    float minLuma = dot(endpoint0.rgb, float3(0.2126, 0.7152, 0.0722));
    float maxLuma = minLuma;
    for (int y = 0; y < 4; y++)
    {
        for (int x = 0; x < 4; x++)
        {
            float4 color = SourceColorAtLocalMp(blockX, blockY, x, y);
            float luma = dot(color.rgb, float3(0.2126, 0.7152, 0.0722));
            if (luma < minLuma)
            {
                endpoint0 = color;
                minLuma = luma;
            }
            if (luma > maxLuma)
            {
                endpoint1 = color;
                maxLuma = luma;
            }
        }
    }
}

int DominantComponentChannelMp(float4 minColor, float4 maxColor)
{
    // RGBAで最もrangeが大きいchannelを選び、単一軸のgradient候補を作る
    float4 range = maxColor - minColor;
    int channel = 0;
    float bestRange = range.r;
    if (range.g > bestRange)
    {
        channel = 1;
        bestRange = range.g;
    }
    if (range.b > bestRange)
    {
        channel = 2;
        bestRange = range.b;
    }
    if (range.a > bestRange)
    {
        channel = 3;
    }

    return channel;
}

void GetComponentEndpointsMp(int blockX, int blockY, int channel, out float4 endpoint0, out float4 endpoint1)
{
    endpoint0 = SourceColorAtLocalMp(blockX, blockY, 0, 0);
    endpoint1 = endpoint0;
    float minValue = ComponentMp(endpoint0, channel);
    float maxValue = minValue;
    for (int y = 0; y < 4; y++)
    {
        for (int x = 0; x < 4; x++)
        {
            float4 color = SourceColorAtLocalMp(blockX, blockY, x, y);
            float value = ComponentMp(color, channel);
            if (value < minValue)
            {
                endpoint0 = color;
                minValue = value;
            }
            if (value > maxValue)
            {
                endpoint1 = color;
                maxValue = value;
            }
        }
    }
}

void GetRgbProjectionEndpointsMp(int blockX, int blockY, out float4 endpoint0, out float4 endpoint1)
{
    // alphaに引っ張られないRGB bounding-box対角へ射影し、その両端texelを候補にする
    float4 minColor;
    float4 maxColor;
    GetColorBoundsMp(blockX, blockY, minColor, maxColor);
    float3 direction = maxColor.rgb - minColor.rgb;
    endpoint0 = minColor;
    endpoint1 = maxColor;
    if (dot(direction, direction) <= 0.000001)
    {
        return;
    }

    endpoint0 = SourceColorAtLocalMp(blockX, blockY, 0, 0);
    endpoint1 = endpoint0;
    float minProjection = dot(endpoint0.rgb - minColor.rgb, direction);
    float maxProjection = minProjection;
    for (int y = 0; y < 4; y++)
    {
        for (int x = 0; x < 4; x++)
        {
            float4 color = SourceColorAtLocalMp(blockX, blockY, x, y);
            float projection = dot(color.rgb - minColor.rgb, direction);
            if (projection < minProjection)
            {
                endpoint0 = color;
                minProjection = projection;
            }
            if (projection > maxProjection)
            {
                endpoint1 = color;
                maxProjection = projection;
            }
        }
    }
}

void GetOutsetEndpointsMp(float4 source0, float4 source1, float scale, out float4 endpoint0, out float4 endpoint1)
{
    // index補間で色rangeが内側へ縮む場合を補うため、endpointを両外側へ広げる
    float4 delta = source1 - source0;
    endpoint0 = saturate(source0 - delta * scale);
    endpoint1 = saturate(source1 + delta * scale);
}

void GetCandidateEndpointsBatch0Mp(int blockX, int blockY, int candidateIndex, out float4 endpoint0, out float4 endpoint1)
{
    endpoint0 = 0.0;
    endpoint1 = 0.0;
    if (candidateIndex == 0) GetOrderedEndpointsMp(blockX, blockY, endpoint0, endpoint1);
    else if (candidateIndex == 1) GetRgbProjectionEndpointsMp(blockX, blockY, endpoint0, endpoint1);
    else if (candidateIndex == 2) GetLumaEndpointsMp(blockX, blockY, endpoint0, endpoint1);
    else
    {
        float4 minColor;
        float4 maxColor;
        GetColorBoundsMp(blockX, blockY, minColor, maxColor);
        GetComponentEndpointsMp(blockX, blockY, DominantComponentChannelMp(minColor, maxColor), endpoint0, endpoint1);
    }
}

void GetCandidateEndpointsBatch1Mp(int blockX, int blockY, int candidateIndex, out float4 endpoint0, out float4 endpoint1)
{
    float4 minColor;
    float4 maxColor;
    GetColorBoundsMp(blockX, blockY, minColor, maxColor);
    float4 base0 = 0.0;
    float4 base1 = 0.0;
    if (candidateIndex == 0)
    {
        GetOutsetEndpointsMp(minColor, maxColor, 0.0625, endpoint0, endpoint1);
        return;
    }
    if (candidateIndex == 1) GetOrderedEndpointsMp(blockX, blockY, base0, base1);
    else if (candidateIndex == 2) GetRgbProjectionEndpointsMp(blockX, blockY, base0, base1);
    else GetComponentEndpointsMp(blockX, blockY, DominantComponentChannelMp(minColor, maxColor), base0, base1);
    GetOutsetEndpointsMp(base0, base1, 0.125, endpoint0, endpoint1);
}

void GetCandidateEndpointsBatch2Mp(int blockX, int blockY, int candidateIndex, out float4 endpoint0, out float4 endpoint1)
{
    float4 base0;
    float4 base1;
    if (candidateIndex == 0) GetOrderedEndpointsMp(blockX, blockY, base0, base1);
    else if (candidateIndex == 1) GetRgbProjectionEndpointsMp(blockX, blockY, base0, base1);
    else if (candidateIndex == 2) GetLumaEndpointsMp(blockX, blockY, base0, base1);
    else GetOrderedEndpointsMp(blockX, blockY, base0, base1);
    float scale = candidateIndex < 2 ? 0.03125 : 0.0625;
    GetOutsetEndpointsMp(base0, base1, scale, endpoint0, endpoint1);
}

void GetCandidateEndpointsBatch3Mp(int blockX, int blockY, int candidateIndex, out float4 endpoint0, out float4 endpoint1)
{
    endpoint0 = 0.0;
    endpoint1 = 0.0;
    float4 minColor;
    float4 maxColor;
    GetColorBoundsMp(blockX, blockY, minColor, maxColor);
    float4 base0 = 0.0;
    float4 base1 = 0.0;
    if (candidateIndex == 0)
    {
        GetRgbProjectionEndpointsMp(blockX, blockY, base0, base1);
        GetOutsetEndpointsMp(base0, base1, 0.0625, endpoint0, endpoint1);
        return;
    }
    if (candidateIndex == 1)
    {
        GetComponentEndpointsMp(blockX, blockY, DominantComponentChannelMp(minColor, maxColor), base0, base1);
        GetOutsetEndpointsMp(base0, base1, 0.0625, endpoint0, endpoint1);
        return;
    }
    if (candidateIndex == 2)
    {
        GetOutsetEndpointsMp(minColor, maxColor, 0.125, endpoint0, endpoint1);
        return;
    }
    GetLumaEndpointsMp(blockX, blockY, base0, base1);
    GetOutsetEndpointsMp(base0, base1, 0.125, endpoint0, endpoint1);
}

float4 ReadCandidateEndpointMp(int blockX, int candidateIndex, int endpointIndex, int blockY)
{
    // Pass 0の横配置は「block → candidate → endpoint」の順。Point samplingで値をそのまま読む
    int pixelX = blockX * BC7_CANDIDATE_ENDPOINT_PIXELS + candidateIndex * 2 + endpointIndex;
    // 候補Texture全体の幅
    float2 uv = (float2(pixelX, blockY) + 0.5) / float2(_CandidateOutputWidth, _OutputHeight);
    return saturate(tex2Dlod(_CandidateTex, float4(uv, 0.0, 0.0)));
}

float4 ReadBestPixelMp(int blockX, int localIndex, int blockY)
{
    // Pass 2は1 blockにつきendpoint0、endpoint1、候補番号/誤差の3pixelを並べる
    int pixelX = blockX * BC7_BEST_PIXELS + localIndex;
    // 最良候補Textureの幅
    float2 uv = (float2(pixelX, blockY) + 0.5) / float2(_BestOutputWidth, _OutputHeight);
    return saturate(tex2Dlod(_BestCandidateTex, float4(uv, 0.0, 0.0)));
}

float4 ReadBaseCandidateEndpointMp(int blockX, int candidateIndex, int endpointIndex, int blockY)
{
    int pixelX = blockX * BC7_CANDIDATE_ENDPOINT_PIXELS + candidateIndex * 2 + endpointIndex;
    // 候補Texture全体の幅
    float2 uv = (float2(pixelX, blockY) + 0.5) / float2(_CandidateOutputWidth, _OutputHeight);
    return saturate(tex2Dlod(_BaseCandidateTex, float4(uv, 0.0, 0.0)));
}

float4 ReadPreviousBestPixelMp(int blockX, int localIndex, int blockY)
{
    int pixelX = blockX * BC7_BEST_PIXELS + localIndex;
    // 最良候補Textureの幅
    float2 uv = (float2(pixelX, blockY) + 0.5) / float2(_BestOutputWidth, _OutputHeight);
    return tex2Dlod(_PreviousBestCandidateTex, float4(uv, 0.0, 0.0));
}

int QuantizedEndpoint7ForPBitMp(int value, int pBit)
{
    return clamp((value - pBit + 1) >> 1, 0, 127);
}

int ReconstructedEndpointByteForPBitMp(int value, int pBit)
{
    return (QuantizedEndpoint7ForPBitMp(value, pBit) << 1) | pBit;
}

int EndpointPBitMp(float4 endpoint)
{
    // endpointごとの共有P-bitをRGBA量子化誤差から選ぶ。opaque alphaはRGB選択から除外する
    int error0 = 0;
    int error1 = 0;
    int alphaWeight = EndpointByteMp(endpoint, 3) >= 254 ? 0 : 1;
    for (int channel = 0; channel < 4; channel++)
    {
        int value = EndpointByteMp(endpoint, channel);
        int delta0 = value - ReconstructedEndpointByteForPBitMp(value, 0);
        int delta1 = value - ReconstructedEndpointByteForPBitMp(value, 1);
        int weight = channel == 3 ? alphaWeight : 1;
        error0 += delta0 * delta0 * weight;
        error1 += delta1 * delta1 * weight;
    }

    return error1 < error0 ? 1 : 0;
}

int QuantizedEndpoint7Mp(float4 endpoint0, float4 endpoint1, int fieldIndex)
{
    int channel = fieldIndex >> 1;
    int endpointIndex = fieldIndex - channel * 2;
    float4 endpoint = endpointIndex == 0 ? endpoint0 : endpoint1;
    int pBit = EndpointPBitMp(endpoint);
    return QuantizedEndpoint7ForPBitMp(EndpointByteMp(endpoint, channel), pBit);
}

int EndpointPBitMp(float4 endpoint0, float4 endpoint1, int endpointIndex)
{
    return EndpointPBitMp(endpointIndex == 0 ? endpoint0 : endpoint1);
}

float4 QuantizedEndpointColorMp(float4 endpoint)
{
    int pBit = EndpointPBitMp(endpoint);
    return float4(
        ReconstructedEndpointByteForPBitMp(EndpointByteMp(endpoint, 0), pBit) / 255.0,
        ReconstructedEndpointByteForPBitMp(EndpointByteMp(endpoint, 1), pBit) / 255.0,
        ReconstructedEndpointByteForPBitMp(EndpointByteMp(endpoint, 2), pBit) / 255.0,
        ReconstructedEndpointByteForPBitMp(EndpointByteMp(endpoint, 3), pBit) / 255.0);
}

int Bc7WeightMp(int index)
{
    // BC7 decoderと同じ4bit index→0..64補間weight table
    if (index <= 0) return 0;
    if (index == 1) return 4;
    if (index == 2) return 9;
    if (index == 3) return 13;
    if (index == 4) return 17;
    if (index == 5) return 21;
    if (index == 6) return 26;
    if (index == 7) return 30;
    if (index == 8) return 34;
    if (index == 9) return 38;
    if (index == 10) return 43;
    if (index == 11) return 47;
    if (index == 12) return 51;
    if (index == 13) return 55;
    if (index == 14) return 60;
    return 64;
}

float4 DecodePaletteColorMp(float4 endpoint0, float4 endpoint1, int index)
{
    return lerp(endpoint0, endpoint1, Bc7WeightMp(index) / 64.0);
}

int QuantizedIndexInRangeMp(int blockX, int blockY, float4 endpoint0, float4 endpoint1, int pixelIndex, int maxIndex)
{
    // payload領域で各indexを仮decode後、入力sampling領域へ戻してから全channel誤差を比較する
    int localX = pixelIndex & 3;
    int localY = pixelIndex >> 2;
    float4 color = SourceColorAtLocalMp(blockX, blockY, localX, localY);
    float4 sourceLinear = SourceLinearColorAtLocalMp(blockX, blockY, localX, localY);
    float4 quantizedEndpoint0 = QuantizedEndpointColorMp(endpoint0);
    float4 quantizedEndpoint1 = QuantizedEndpointColorMp(endpoint1);
    int projectedIndex = (int)round(ProjectWeightMp(color, quantizedEndpoint0, quantizedEndpoint1) * 15.0);
    int bestIndex = projectedIndex;
    int bestError = 2147483647;
    for (int index = 0; index <= maxIndex; index++)
    {
        float4 decoded = DecodePaletteColorMp(quantizedEndpoint0, quantizedEndpoint1, index);
        float4 decodedSourceColor = StorageToSourceColorMp(decoded);
        float4 delta = sourceLinear - decodedSourceColor;
        int error = (int)round(dot(delta, delta) * 1000000.0);
        if (error < bestError)
        {
            bestError = error;
            bestIndex = index;
        }
    }

    return bestIndex;
}

int QuantizedIndexMp(int blockX, int blockY, float4 endpoint0, float4 endpoint1, int pixelIndex)
{
    return QuantizedIndexInRangeMp(blockX, blockY, endpoint0, endpoint1, pixelIndex, pixelIndex == 0 ? 7 : 15);
}

int UnrestrictedQuantizedIndexMp(int blockX, int blockY, float4 endpoint0, float4 endpoint1, int pixelIndex)
{
    return QuantizedIndexInRangeMp(blockX, blockY, endpoint0, endpoint1, pixelIndex, 15);
}

float BlockErrorMp(int blockX, int blockY, float4 endpoint0, float4 endpoint1)
{
    // endpoint量子化とindex選択後の16texelを仮復元し、候補比較用のRGBA二乗誤差を返す
    float4 quantizedEndpoint0 = QuantizedEndpointColorMp(endpoint0);
    float4 quantizedEndpoint1 = QuantizedEndpointColorMp(endpoint1);
    float error = 0.0;
    for (int pixelIndex = 0; pixelIndex < 16; pixelIndex++)
    {
        int localX = pixelIndex & 3;
        int localY = pixelIndex >> 2;
        float4 source = SourceLinearColorAtLocalMp(blockX, blockY, localX, localY);
        float4 decoded = DecodePaletteColorMp(quantizedEndpoint0, quantizedEndpoint1, QuantizedIndexMp(blockX, blockY, endpoint0, endpoint1, pixelIndex));
        float4 decodedSourceColor = StorageToSourceColorMp(decoded);
        float4 delta = source - decodedSourceColor;
        error += dot(delta, delta);
    }

    return error;
}

void EnsureFixupTexelEndpointOrderMp(int blockX, int blockY, inout float4 endpoint0, inout float4 endpoint1)
{
    // fix-up texel 0のindexが3bitへ収まる向きになるようendpoint順を交換する
    if (UnrestrictedQuantizedIndexMp(blockX, blockY, endpoint0, endpoint1, 0) <= 7)
    {
        return;
    }

    float4 temp = endpoint0;
    endpoint0 = endpoint1;
    endpoint1 = temp;
}

void TryLeastSquaresEndpointFitMp(int blockX, int blockY, float4 baseEndpoint0, float4 baseEndpoint1, inout float4 bestEndpoint0, inout float4 bestEndpoint1, inout float bestError)
{
    // indexを固定した最小二乗fitでendpointを再推定し、量子化後も改善する場合だけ採用する
    float sumA2 = 0.0;
    float sumAB = 0.0;
    float sumB2 = 0.0;
    float4 sumAC = 0.0;
    float4 sumBC = 0.0;
    for (int pixelIndex = 0; pixelIndex < 16; pixelIndex++)
    {
        int localX = pixelIndex & 3;
        int localY = pixelIndex >> 2;
        float4 color = SourceColorAtLocalMp(blockX, blockY, localX, localY);
        int index = QuantizedIndexMp(blockX, blockY, baseEndpoint0, baseEndpoint1, pixelIndex);
        float b = Bc7WeightMp(index) / 64.0;
        float a = 1.0 - b;
        sumA2 += a * a;
        sumAB += a * b;
        sumB2 += b * b;
        sumAC += color * a;
        sumBC += color * b;
    }

    float determinant = sumA2 * sumB2 - sumAB * sumAB;
    if (abs(determinant) <= 0.000001)
    {
        return;
    }

    float invDeterminant = 1.0 / determinant;
    float4 candidate0 = saturate((sumAC * sumB2 - sumBC * sumAB) * invDeterminant);
    float4 candidate1 = saturate((sumBC * sumA2 - sumAC * sumAB) * invDeterminant);
    EnsureFixupTexelEndpointOrderMp(blockX, blockY, candidate0, candidate1);
    float error = BlockErrorMp(blockX, blockY, candidate0, candidate1);
    if (error < bestError)
    {
        bestError = error;
        bestEndpoint0 = candidate0;
        bestEndpoint1 = candidate1;
    }
}

void TryEndpointScaleMp(int blockX, int blockY, float4 baseEndpoint0, float4 baseEndpoint1, float scale, inout float4 bestEndpoint0, inout float4 bestEndpoint1, inout float bestError)
{
    // endpoint中心を維持したrange拡縮候補を評価する
    float4 center = (baseEndpoint0 + baseEndpoint1) * 0.5;
    float4 halfRange = (baseEndpoint1 - baseEndpoint0) * (0.5 * scale);
    float4 candidate0 = saturate(center - halfRange);
    float4 candidate1 = saturate(center + halfRange);
    EnsureFixupTexelEndpointOrderMp(blockX, blockY, candidate0, candidate1);
    float error = BlockErrorMp(blockX, blockY, candidate0, candidate1);
    if (error < bestError)
    {
        bestError = error;
        bestEndpoint0 = candidate0;
        bestEndpoint1 = candidate1;
    }
}

void TryEndpointNudgeMp(int blockX, int blockY, float4 baseEndpoint0, float4 baseEndpoint1, float endpoint0Steps, float endpoint1Steps, inout float4 bestEndpoint0, inout float4 bestEndpoint1, inout float bestError)
{
    // endpoint軸に沿って8bit量子化の1～2段階だけ移動し、局所的な誤差改善を探す
    float4 axis = baseEndpoint1 - baseEndpoint0;
    float axisLengthSq = dot(axis, axis);
    if (axisLengthSq <= 0.000001)
    {
        return;
    }

    axis *= rsqrt(axisLengthSq);
    float4 candidate0 = saturate(baseEndpoint0 + axis * (endpoint0Steps / 255.0));
    float4 candidate1 = saturate(baseEndpoint1 + axis * (endpoint1Steps / 255.0));
    EnsureFixupTexelEndpointOrderMp(blockX, blockY, candidate0, candidate1);
    float error = BlockErrorMp(blockX, blockY, candidate0, candidate1);
    if (error < bestError)
    {
        bestError = error;
        bestEndpoint0 = candidate0;
        bestEndpoint1 = candidate1;
    }
}

int DivideBy7SmallMp(int value)
{
    if (value >= 49) return 7;
    if (value >= 42) return 6;
    if (value >= 35) return 5;
    if (value >= 28) return 4;
    if (value >= 21) return 3;
    if (value >= 14) return 2;
    if (value >= 7) return 1;
    return 0;
}

int Bc7Mode6BitMp(int blockX, int blockY, float4 endpoint0, float4 endpoint1, int bitIndex)
{
    // mode 6の128bitを、mode prefix→endpoint→P-bit→indexの順で1bitずつ生成する
    // 戻り値を先に初期化し、Shader compilerが分岐後の値を未初期化と誤判定しない形にする
    int outputBit = 0;
    if (bitIndex < 6)
    {
        outputBit = 0;
    }
    else if (bitIndex == 6)
    {
        outputBit = 1;
    }
    else if (bitIndex < 63)
    {
        int endpointBit = bitIndex - 7;
        int fieldIndex = DivideBy7SmallMp(endpointBit);
        int fieldBit = endpointBit - fieldIndex * 7;
        outputBit = (QuantizedEndpoint7Mp(endpoint0, endpoint1, fieldIndex) >> fieldBit) & 1;
    }
    else if (bitIndex == 63)
    {
        outputBit = EndpointPBitMp(endpoint0, endpoint1, 0);
    }
    else if (bitIndex == 64)
    {
        outputBit = EndpointPBitMp(endpoint0, endpoint1, 1);
    }
    else
    {
        int indexBit = bitIndex - 65;
        if (indexBit < 3)
        {
            outputBit = (QuantizedIndexMp(blockX, blockY, endpoint0, endpoint1, 0) >> indexBit) & 1;
        }
        else
        {
            int remaining = indexBit - 3;
            int pixelIndex = min((remaining >> 2) + 1, 15);
            int pixelBit = remaining - (pixelIndex - 1) * 4;
            outputBit = (QuantizedIndexMp(blockX, blockY, endpoint0, endpoint1, pixelIndex) >> pixelBit) & 1;
        }
    }

    return outputBit;
}

int Bc7Mode6ByteMp(int blockX, int blockY, float4 endpoint0, float4 endpoint1, int byteIndex)
{
    // 8個のbitをLSB-firstで1byteへまとめる
    int result = 0;
    int bitBase = byteIndex * 8;
    for (int bit = 0; bit < 8; bit++)
    {
        result |= Bc7Mode6BitMp(blockX, blockY, endpoint0, endpoint1, bitBase + bit) << bit;
    }

    return result;
}

float ByteToReadbackColorMp(int value)
{
    // RGBA32 readbackで同じbyteへ戻るよう量子化cellの内側へ値を置く
    return saturate((value + 0.25) / 255.0);
}

float4 CandidateEndpointsPairFragMp(MpVaryings i, int sourceBatch, int sourceCandidateOffset)
{
    // 候補Texture全体の幅
    int2 outputPixel = OutputPixelMp(i.uv, _CandidateOutputWidth, _OutputHeight);
    int blockX = outputPixel.x >> 2;
    int local = outputPixel.x - blockX * BC7_CANDIDATE_ENDPOINT_PIXELS;
    int sourceCandidateIndex = (local >> 1) + sourceCandidateOffset;
    float4 endpoint0 = 0.0;
    float4 endpoint1 = 0.0;
    if (sourceBatch == 0) GetCandidateEndpointsBatch0Mp(blockX, outputPixel.y, sourceCandidateIndex, endpoint0, endpoint1);
    else if (sourceBatch == 1) GetCandidateEndpointsBatch1Mp(blockX, outputPixel.y, sourceCandidateIndex, endpoint0, endpoint1);
    else if (sourceBatch == 2) GetCandidateEndpointsBatch2Mp(blockX, outputPixel.y, sourceCandidateIndex, endpoint0, endpoint1);
    else GetCandidateEndpointsBatch3Mp(blockX, outputPixel.y, sourceCandidateIndex, endpoint0, endpoint1);
    return (local & 1) == 0 ? endpoint0 : endpoint1;
}

float4 fragCandidateEndpointsBatch0(MpVaryings i) : SV_Target
{
    return CandidateEndpointsPairFragMp(i, 0, 0);
}

float4 fragCandidateEndpointsBatch1(MpVaryings i) : SV_Target
{
    return CandidateEndpointsPairFragMp(i, 0, 2);
}

float4 fragCandidateEndpointsBatch2(MpVaryings i) : SV_Target
{
    return CandidateEndpointsPairFragMp(i, 1, 0);
}

float4 fragCandidateEndpointsBatch3(MpVaryings i) : SV_Target
{
    return CandidateEndpointsPairFragMp(i, 1, 2);
}

float4 fragCandidateEndpointsBatch4(MpVaryings i) : SV_Target
{
    return CandidateEndpointsPairFragMp(i, 2, 0);
}

float4 fragCandidateEndpointsBatch5(MpVaryings i) : SV_Target
{
    return CandidateEndpointsPairFragMp(i, 2, 2);
}

float4 fragCandidateEndpointsBatch6(MpVaryings i) : SV_Target
{
    return CandidateEndpointsPairFragMp(i, 3, 0);
}

float4 fragCandidateEndpointsBatch7(MpVaryings i) : SV_Target
{
    return CandidateEndpointsPairFragMp(i, 3, 2);
}

float4 fragLeastSquares(MpVaryings i) : SV_Target
{
    // 候補Texture全体の幅
    int2 outputPixel = OutputPixelMp(i.uv, _CandidateOutputWidth, _OutputHeight);
    int blockX = outputPixel.x >> 2;
    int local = outputPixel.x - blockX * BC7_CANDIDATE_ENDPOINT_PIXELS;
    int candidateIndex = local >> 1;
    int endpointIndex = local & 1;
    float4 endpoint0 = ReadCandidateEndpointMp(blockX, candidateIndex, 0, outputPixel.y);
    float4 endpoint1 = ReadCandidateEndpointMp(blockX, candidateIndex, 1, outputPixel.y);
    EnsureFixupTexelEndpointOrderMp(blockX, outputPixel.y, endpoint0, endpoint1);
    float bestError = BlockErrorMp(blockX, outputPixel.y, endpoint0, endpoint1);
    TryLeastSquaresEndpointFitMp(blockX, outputPixel.y, endpoint0, endpoint1, endpoint0, endpoint1, bestError);
    EnsureFixupTexelEndpointOrderMp(blockX, outputPixel.y, endpoint0, endpoint1);
    return endpointIndex == 0 ? endpoint0 : endpoint1;
}

float4 fragScaleSearch(MpVaryings i) : SV_Target
{
    // 候補Texture全体の幅
    int2 outputPixel = OutputPixelMp(i.uv, _CandidateOutputWidth, _OutputHeight);
    int blockX = outputPixel.x >> 2;
    int local = outputPixel.x - blockX * BC7_CANDIDATE_ENDPOINT_PIXELS;
    int candidateIndex = local >> 1;
    int endpointIndex = local & 1;
    float4 baseEndpoint0 = ReadCandidateEndpointMp(blockX, candidateIndex, 0, outputPixel.y);
    float4 baseEndpoint1 = ReadCandidateEndpointMp(blockX, candidateIndex, 1, outputPixel.y);
    float4 endpoint0 = baseEndpoint0;
    float4 endpoint1 = baseEndpoint1;
    float bestError = BlockErrorMp(blockX, outputPixel.y, endpoint0, endpoint1);
    TryEndpointScaleMp(blockX, outputPixel.y, baseEndpoint0, baseEndpoint1, 0.75, endpoint0, endpoint1, bestError);
    TryEndpointScaleMp(blockX, outputPixel.y, baseEndpoint0, baseEndpoint1, 0.875, endpoint0, endpoint1, bestError);
    TryEndpointScaleMp(blockX, outputPixel.y, baseEndpoint0, baseEndpoint1, 0.9375, endpoint0, endpoint1, bestError);
    TryEndpointScaleMp(blockX, outputPixel.y, baseEndpoint0, baseEndpoint1, 0.96875, endpoint0, endpoint1, bestError);
    TryEndpointScaleMp(blockX, outputPixel.y, baseEndpoint0, baseEndpoint1, 1.03125, endpoint0, endpoint1, bestError);
    TryEndpointScaleMp(blockX, outputPixel.y, baseEndpoint0, baseEndpoint1, 1.0625, endpoint0, endpoint1, bestError);
    TryEndpointScaleMp(blockX, outputPixel.y, baseEndpoint0, baseEndpoint1, 1.125, endpoint0, endpoint1, bestError);
    TryEndpointScaleMp(blockX, outputPixel.y, baseEndpoint0, baseEndpoint1, 1.1875, endpoint0, endpoint1, bestError);
    TryEndpointScaleMp(blockX, outputPixel.y, baseEndpoint0, baseEndpoint1, 1.25, endpoint0, endpoint1, bestError);
    TryEndpointScaleMp(blockX, outputPixel.y, baseEndpoint0, baseEndpoint1, 1.375, endpoint0, endpoint1, bestError);
    TryEndpointScaleMp(blockX, outputPixel.y, baseEndpoint0, baseEndpoint1, 1.5, endpoint0, endpoint1, bestError);
    return endpointIndex == 0 ? endpoint0 : endpoint1;
}

float4 fragNudgeSearch(MpVaryings i) : SV_Target
{
    // 候補Texture全体の幅
    int2 outputPixel = OutputPixelMp(i.uv, _CandidateOutputWidth, _OutputHeight);
    int blockX = outputPixel.x >> 2;
    int local = outputPixel.x - blockX * BC7_CANDIDATE_ENDPOINT_PIXELS;
    int candidateIndex = local >> 1;
    int endpointIndex = local & 1;
    float4 baseEndpoint0 = ReadBaseCandidateEndpointMp(blockX, candidateIndex, 0, outputPixel.y);
    float4 baseEndpoint1 = ReadBaseCandidateEndpointMp(blockX, candidateIndex, 1, outputPixel.y);
    float4 endpoint0 = ReadCandidateEndpointMp(blockX, candidateIndex, 0, outputPixel.y);
    float4 endpoint1 = ReadCandidateEndpointMp(blockX, candidateIndex, 1, outputPixel.y);
    float bestError = BlockErrorMp(blockX, outputPixel.y, endpoint0, endpoint1);
    TryEndpointNudgeMp(blockX, outputPixel.y, baseEndpoint0, baseEndpoint1, -1.0, -1.0, endpoint0, endpoint1, bestError);
    TryEndpointNudgeMp(blockX, outputPixel.y, baseEndpoint0, baseEndpoint1, 1.0, 1.0, endpoint0, endpoint1, bestError);
    TryEndpointNudgeMp(blockX, outputPixel.y, baseEndpoint0, baseEndpoint1, -1.0, 1.0, endpoint0, endpoint1, bestError);
    TryEndpointNudgeMp(blockX, outputPixel.y, baseEndpoint0, baseEndpoint1, 1.0, -1.0, endpoint0, endpoint1, bestError);
    TryEndpointNudgeMp(blockX, outputPixel.y, baseEndpoint0, baseEndpoint1, -2.0, -2.0, endpoint0, endpoint1, bestError);
    TryEndpointNudgeMp(blockX, outputPixel.y, baseEndpoint0, baseEndpoint1, 2.0, 2.0, endpoint0, endpoint1, bestError);
    EnsureFixupTexelEndpointOrderMp(blockX, outputPixel.y, endpoint0, endpoint1);
    return endpointIndex == 0 ? endpoint0 : endpoint1;
}

float4 fragBestCandidate(MpVaryings i) : SV_Target
{
    // 最良候補Textureの幅
    int2 outputPixel = OutputPixelMp(i.uv, _BestOutputWidth, _OutputHeight);
    int blockX = (int)floor((float)outputPixel.x * 0.3333333333);
    int local = outputPixel.x - blockX * BC7_BEST_PIXELS;
    int bestCandidate = 0;
    float bestError = 1000000.0;
    for (int candidateIndex = 0; candidateIndex < BC7_CANDIDATE_BATCH_SIZE; candidateIndex++)
    {
        float4 candidate0 = ReadCandidateEndpointMp(blockX, candidateIndex, 0, outputPixel.y);
        float4 candidate1 = ReadCandidateEndpointMp(blockX, candidateIndex, 1, outputPixel.y);
        float error = BlockErrorMp(blockX, outputPixel.y, candidate0, candidate1);
        if (error < bestError)
        {
            bestError = error;
            bestCandidate = candidateIndex;
        }
    }

    float4 endpoint0 = ReadCandidateEndpointMp(blockX, bestCandidate, 0, outputPixel.y);
    float4 endpoint1 = ReadCandidateEndpointMp(blockX, bestCandidate, 1, outputPixel.y);
    float refinedError = bestError;
    if (_HasPreviousBest > 0.5)
    {
        float4 previousMetadata = ReadPreviousBestPixelMp(blockX, 2, outputPixel.y);
        if (previousMetadata.g <= refinedError)
        {
            return ReadPreviousBestPixelMp(blockX, local, outputPixel.y);
        }
    }
    if (local == 0)
    {
        return endpoint0;
    }
    if (local == 1)
    {
        return endpoint1;
    }

    return float4((_CandidateBatchOffset + bestCandidate) / 255.0, refinedError, 0.0, 1.0);
}

float4 fragPackBytes(MpVaryings i) : SV_Target
{
    // Pass 3: 横4pixelの各RGBAへ4byteずつ格納し、1 blockの16byteを完成させる
    int2 outputPixel = OutputPixelMp(i.uv, _OutputWidth, _OutputHeight);
    int blockX = outputPixel.x >> 2;
    int blockY = outputPixel.y;
    int byteBase = (outputPixel.x & 3) * 4;
    float4 endpoint0 = ReadBestPixelMp(blockX, 0, blockY);
    float4 endpoint1 = ReadBestPixelMp(blockX, 1, blockY);
    return float4(
        ByteToReadbackColorMp(Bc7Mode6ByteMp(blockX, blockY, endpoint0, endpoint1, byteBase)),
        ByteToReadbackColorMp(Bc7Mode6ByteMp(blockX, blockY, endpoint0, endpoint1, byteBase + 1)),
        ByteToReadbackColorMp(Bc7Mode6ByteMp(blockX, blockY, endpoint0, endpoint1, byteBase + 2)),
        ByteToReadbackColorMp(Bc7Mode6ByteMp(blockX, blockY, endpoint0, endpoint1, byteBase + 3)));
}
