/*

  *******  Super XBR Shader (pass0, pass1, pass2)  *******

  Copyright (c) 2015 Hyllian - sergiogdb@gmail.com

  Permission is hereby granted, free of charge, to any person obtaining a copy
  of this software and associated documentation files (the "Software"), to deal
  in the Software without restriction, including without limitation the rights
  to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
  copies of the Software, and to permit persons to whom the Software is
  furnished to do so, subject to the following conditions:

  The above copyright notice and this permission notice shall be included in
  all copies or substantial portions of the Software.

  THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
  IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
  FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
  AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
  LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
  OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
  THE SOFTWARE.

  HLSL (Direct3D 11, ps_5_0) port of libretro slang-shaders
  edge-smoothing/xbr/shaders/super-xbr/super-xbr-pass{0,1,2}.slang.
  The preset's leading luma pass (aliased XbrSource) is folded in: every
  SOURCE tap computes luma into alpha on the fly. The final jinc2/deblur
  passes are omitted; the host scales the 2x result itself.
*/

cbuffer G : register(b0)
{
    float4 OutSize;  // this pass output: w, h, 1/w, 1/h
    float4 In0Size;  // the pass's first input texture: w, h, 1/w, 1/h
    float4 SrcSize;  // the SOURCE texture: w, h, 1/w, 1/h
    float4 Pad;
};
Texture2D T0 : register(t0);
Texture2D T1 : register(t1);
Texture2D T2 : register(t2);
Texture2D T3 : register(t3);
SamplerState Pt : register(s0);   // point, clamp
SamplerState Lin : register(s1);  // linear, clamp
struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; };  // uv 0..1 over the pass OUTPUT, (0,0) = top-left

// ---- Parameters (defaults of the #pragma parameter lines) ----
// Note: libretro's super-xbr.slangp overrides all three edge strengths to 3.0.
static const float MODE            = 1.0;  // 0 Normal, 1 Details, 2 Adaptive
static const float XBR_EDGE_STR_P0 = 5.0;
static const float XBR_EDGE_STR_P1 = 5.0;
static const float XBR_EDGE_STR_P2 = 5.0;
static const float XBR_WEIGHT_P0   = 0.0;  // pass0 runtime parameter (default 0.0)
static const float XBR_WEIGHT      = 1.0;  // hard-coded in pass1 / pass2
static const float XBR_EDGE_SHP    = 0.4;
static const float XBR_TEXTURE_SHP = 1.0;

static const float3 Y = float3(.2126, .7152, .0722);

float RGBtoYUV(float3 color) { return dot(color, Y); }

float df(float A, float B) { return abs(A - B); }

/*
                              P1
     |P0|B |C |P1|         C     F4          |a0|b1|c2|d3|
     |D |E |F |F4|      B     F     I4       |b0|c1|d2|e3|   |e1|i1|i2|e2|
     |G |H |I |I4|   P0    E  A  I     P3    |c0|d1|e2|f3|   |e3|i3|i4|e4|
     |P2|H5|I5|P3|      D     H     I5       |d0|e1|f2|g3|
                           G     H5
                              P2
*/

float d_wd(float wp1, float wp2, float wp3, float wp4, float wp5, float wp6, float b0, float b1, float c0, float c1, float c2, float d0, float d1, float d2, float d3, float e1, float e2, float e3, float f2, float f3)
{
    return (wp1*(df(c1,c2) + df(c1,c0) + df(e2,e1) + df(e2,e3)) + wp2*(df(d2,d3) + df(d0,d1)) + wp3*(df(d1,d3) + df(d0,d2)) + wp4*df(d1,d2) + wp5*(df(c0,c2) + df(e1,e3)) + wp6*(df(b0,b1) + df(f2,f3)));
}

float hv_wd(float wp1, float wp2, float wp3, float wp4, float wp5, float wp6, float i1, float i2, float i3, float i4, float e1, float e2, float e3, float e4)
{
    return ( wp4*(df(i1,i2)+df(i3,i4)) + wp1*(df(i1,e1)+df(i2,e2)+df(i3,e3)+df(i4,e4)) + wp3*(df(i1,e2)+df(i3,e4)+df(e1,i2)+df(e3,i4)));
}

float3 min4(float3 a, float3 b, float3 c, float3 d) { return min(a, min(b, min(c, d))); }
float3 max4(float3 a, float3 b, float3 c, float3 d) { return max(a, max(b, max(c, d))); }
float max4float(float a, float b, float c, float d) { return max(a, max(b, max(c, d))); }

// GLSL "mat4x3(a,b,c,d)" with the slang "#define mul(a,b) (b*a)" == w.x*a + w.y*b + w.z*c + w.w*d.
float3 wsum(float4 w, float3 a, float3 b, float3 c, float3 d) { return w.x*a + w.y*b + w.z*c + w.w*d; }

// SOURCE tap with luma in alpha (replaces the preset's luma pass / XbrSource).
float4 SrcLuma(float2 uv)
{
    float3 c = T1.SampleLevel(Pt, uv, 0).rgb;
    return float4(c, RGBtoYUV(c));
}

// Shared Super-xBR kernel (identical in all three passes apart from the settings).
float3 XbrCore(float wp1, float wp2, float wp3, float wp4, float wp5, float wp6,
               float weight1, float weight2, float edgeStr,
               float4 P0_, float4 P1_, float4 P2_, float4 P3_,
               float4 B_,  float4 C_,  float4 D_,  float4 E_,
               float4 F_,  float4 G_,  float4 H_,  float4 I_,
               float4 F4_, float4 I4_, float4 H5_, float4 I5_)
{
    float3 P0 = P0_.rgb; float p0 = P0_.a;
    float3 P1 = P1_.rgb; float p1 = P1_.a;
    float3 P2 = P2_.rgb; float p2 = P2_.a;
    float3 P3 = P3_.rgb; float p3 = P3_.a;
    float3 B  = B_.rgb;  float b  = B_.a;
    float3 C  = C_.rgb;  float c  = C_.a;
    float3 D  = D_.rgb;  float d  = D_.a;
    float3 E  = E_.rgb;  float e  = E_.a;
    float3 F  = F_.rgb;  float f  = F_.a;
    float3 G  = G_.rgb;  float g  = G_.a;
    float3 H  = H_.rgb;  float h  = H_.a;
    float3 I  = I_.rgb;  float i  = I_.a;
    float3 F4 = F4_.rgb; float f4 = F4_.a;
    float3 I4 = I4_.rgb; float i4 = I4_.a;
    float3 H5 = H5_.rgb; float h5 = H5_.a;
    float3 I5 = I5_.rgb; float i5 = I5_.a;

    /* Calc edgeness in diagonal directions. */
    float d_edge  = (d_wd( wp1, wp2, wp3, wp4, wp5, wp6, d, b, g, e, c, p2, h, f, p1, h5, i, f4, i5, i4 ) - d_wd( wp1, wp2, wp3, wp4, wp5, wp6, c, f4, b, f, i4, p0, e, i, p3, d, h, i5, g, h5 ));

    /* Calc edgeness in horizontal/vertical directions. */
    float hv_edge = (hv_wd(wp1, wp2, wp3, wp4, wp5, wp6, f, i, e, h, c, i5, b, h5) - hv_wd(wp1, wp2, wp3, wp4, wp5, wp6, e, f, h, i, d, f4, g, i4));

    float limits = edgeStr + 0.000001;
    float edge_strength = smoothstep(0.0, limits, abs(d_edge));

    float4 w1, w2;
    float3 c3, c4;
    if (MODE == 2.0)
    {
        float contrast = max(max4float(df(e,f),df(e,i),df(e,h),df(f,h)),max(df(f,i),df(h,i)))/(e+0.001);

        float wgt1 = weight1*(smoothstep(0.0, 0.6, contrast)*XBR_EDGE_SHP + XBR_TEXTURE_SHP);
        float wgt2 = weight2*(smoothstep(0.0, 0.6, contrast)*XBR_EDGE_SHP + XBR_TEXTURE_SHP);

        /* Filter weights. Two taps only. */
        w1 = float4(-wgt1, wgt1+ 0.5, wgt1+ 0.5, -wgt1);
        w2 = float4(-wgt2, wgt2+0.25, wgt2+0.25, -wgt2);
        c3 = wsum(w2, P0+2.0*(D+G)+P2, B+2.0*(E+H)+H5, C+2.0*(F+I)+I5, P1+2.0*(F4+I4)+P3)/3.0;
        c4 = wsum(w2, P0+2.0*(C+B)+P1, D+2.0*(F+E)+F4, G+2.0*(I+H)+I4, P2+2.0*(I5+H5)+P3)/3.0;
    }
    else
    {
        /* Filter weights. Two taps only. */
        w1 = float4(-weight1, weight1+ 0.5, weight1+ 0.5, -weight1);
        w2 = float4(-weight2, weight2+0.25, weight2+0.25, -weight2);
        c3 = wsum(w2, D+G, E+H, F+I, F4+I4);
        c4 = wsum(w2, C+B, F+E, I+H, I5+H5);
    }

    /* Filtering and normalization in four direction generating four colors. */
    float3 c1 = wsum(w1, P2, H, F, P1);
    float3 c2 = wsum(w1, P0, E, I, P3);

    /* Smoothly blends the two strongest directions (one in diagonal and the other in vert/horiz direction). */
    float3 color = lerp(lerp(c1, c2, step(0.0, d_edge)), lerp(c3, c4, step(0.0, hv_edge)), 1. - edge_strength);

    /* Anti-ringing code. */
    float3 min_sample = min4( E, F, H, I );
    float3 max_sample = max4( E, F, H, I );
    return clamp(color, min_sample, max_sample);
}

// ---------------------------------------------------------------------------
// Pass 0: T0 = SOURCE. Output 1x SOURCE. Stores (rgb, luma).
// ---------------------------------------------------------------------------
float4 SrcLuma0(float2 uv)
{
    float3 c = T0.SampleLevel(Pt, uv, 0).rgb;
    return float4(c, RGBtoYUV(c));
}

float4 PS_Xbr_0(V i) : SV_Target
{
    float wp1 = 2.0, wp2 = 1.0, wp3 = -1.0, wp4 = 4.0, wp5 = -1.0, wp6 = 1.0;
    float weight1 = (XBR_WEIGHT_P0*1.29633/10.0);
    float weight2 = (XBR_WEIGHT_P0*1.75068/10.0/2.0);

    float2 uv = i.uv;
    float dx = In0Size.z;
    float dy = In0Size.w;
    float4 t1 = uv.xyxy + float4(-dx, -dy, 2.0*dx, 2.0*dy);
    float4 t2 = uv.xyxy + float4(  0, -dy,     dx, 2.0*dy);
    float4 t3 = uv.xyxy + float4(-dx,   0, 2.0*dx,     dy);
    float4 t4 = uv.xyxy + float4(  0,   0,     dx,     dy);

    float3 color = XbrCore(wp1, wp2, wp3, wp4, wp5, wp6, weight1, weight2, XBR_EDGE_STR_P0,
        SrcLuma0(t1.xy), SrcLuma0(t1.zy), SrcLuma0(t1.xw), SrcLuma0(t1.zw),   // P0 P1 P2 P3
        SrcLuma0(t2.xy), SrcLuma0(t2.zy),                                     // B C
        SrcLuma0(t3.xy), SrcLuma0(t4.xy), SrcLuma0(t4.zy), SrcLuma0(t3.xw),   // D E F G
        SrcLuma0(t4.xw), SrcLuma0(t4.zw),                                     // H I
        SrcLuma0(t3.zy), SrcLuma0(t3.zw), SrcLuma0(t2.xw), SrcLuma0(t2.zw));  // F4 I4 H5 I5

    return float4(color, RGBtoYUV(color));
}

// ---------------------------------------------------------------------------
// Pass 1: T0 = pass0 output ("Source", 1x), T1 = SOURCE ("XbrSource", 1x).
// Output 2x SOURCE. Stores (rgb, luma).
// ---------------------------------------------------------------------------
float4 PS_Xbr_1(V i) : SV_Target
{
    float wp1, wp2, wp3, wp4, wp5, wp6, weight1, weight2;
    if (MODE == 1.0)
    {
        wp1 = 1.0; wp2 = 0.0; wp3 = 0.0; wp4 = 0.0; wp5 = 0.0; wp6 = 0.0;
        weight1 = (XBR_WEIGHT*1.29633/10.0);
        weight2 = (XBR_WEIGHT*1.75068/10.0/2.0);
    }
    else if (MODE == 2.0)
    {
        wp1 = 8.0; wp2 = 0.0; wp3 = 0.0; wp4 = 0.0; wp5 = 0.0; wp6 = 0.0;
        weight1 = (1.29633/10.0);
        weight2 = (1.75068/10.0/2.0);
    }
    else
    {
        wp1 = 8.0; wp2 = 0.0; wp3 = 0.0; wp4 = 0.0; wp5 = 0.0; wp6 = 0.0;
        weight1 = (XBR_WEIGHT*1.29633/10.0);
        weight2 = (XBR_WEIGHT*1.75068/10.0/2.0);
    }

    float2 uv = i.uv;
    // SourceSize in the slang pass is the size of pass0's output (= SOURCE size).
    float2 srcSize = In0Size.xy;

    // Skip pixels on wrong grid
    float2 fp = frac(uv * srcSize);
    float2 dir = fp - float2(0.5, 0.5);
    if ((dir.x * dir.y) > 0.0)
    {
        // top-left sub-pixel: original SOURCE pixel; bottom-right: pass0 diagonal pixel
        return lerp(SrcLuma(uv), T0.SampleLevel(Pt, uv, 0), step(0.0, dir.x));
    }

    float2 g1 = (fp.x > 0.5) ? float2(0.5 / srcSize.x, 0.0) : float2(0.0, 0.5 / srcSize.y);
    float2 g2 = (fp.x > 0.5) ? float2(0.0, 0.5 / srcSize.y) : float2(0.5 / srcSize.x, 0.0);

    float4 P0_ = SrcLuma(              uv -3.0*g1        );
    float4 P1_ = T0.SampleLevel(Pt,    uv         -3.0*g2, 0);
    float4 P2_ = T0.SampleLevel(Pt,    uv         +3.0*g2, 0);
    float4 P3_ = SrcLuma(              uv +3.0*g1        );

    float4  B_ = T0.SampleLevel(Pt,    uv -2.0*g1     -g2, 0);
    float4  C_ = SrcLuma(              uv     -g1 -2.0*g2);
    float4  D_ = T0.SampleLevel(Pt,    uv -2.0*g1     +g2, 0);
    float4  E_ = SrcLuma(              uv     -g1        );
    float4  F_ = T0.SampleLevel(Pt,    uv             -g2, 0);
    float4  G_ = SrcLuma(              uv     -g1 +2.0*g2);
    float4  H_ = T0.SampleLevel(Pt,    uv             +g2, 0);
    float4  I_ = SrcLuma(              uv     +g1        );

    float4 F4_ = SrcLuma(              uv     +g1 -2.0*g2);
    float4 I4_ = T0.SampleLevel(Pt,    uv +2.0*g1     -g2, 0);
    float4 H5_ = SrcLuma(              uv     +g1 +2.0*g2);
    float4 I5_ = T0.SampleLevel(Pt,    uv +2.0*g1     +g2, 0);

    float3 color = XbrCore(wp1, wp2, wp3, wp4, wp5, wp6, weight1, weight2, XBR_EDGE_STR_P1,
        P0_, P1_, P2_, P3_, B_, C_, D_, E_, F_, G_, H_, I_, F4_, I4_, H5_, I5_);

    return float4(color, RGBtoYUV(color));
}

// ---------------------------------------------------------------------------
// Pass 2: T0 = pass1 output (2x). Output 2x SOURCE (scale 1 of its input).
// ---------------------------------------------------------------------------
float4 PS_Xbr_2(V i) : SV_Target
{
    float wp1, wp2, wp3, wp4, wp5, wp6, weight1, weight2;
    if (MODE == 1.0)
    {
        wp1 = 0.0; wp2 = 0.0; wp3 = 0.0; wp4 = 1.0; wp5 = 0.0; wp6 = 0.0;
        weight1 = (XBR_WEIGHT*1.29633/10.0);
        weight2 = (XBR_WEIGHT*1.75068/10.0/2.0);
    }
    else if (MODE == 2.0)
    {
        wp1 = 1.0; wp2 = 0.0; wp3 = 1.0; wp4 = 3.0; wp5 = -2.0; wp6 = 0.0;
        weight1 = (1.29633/10.0);
        weight2 = (1.75068/10.0/2.0);
    }
    else
    {
        wp1 = 1.0; wp2 = 0.0; wp3 = 2.0; wp4 = 3.0; wp5 = -2.0; wp6 = 1.0;
        weight1 = (XBR_WEIGHT*1.29633/10.0);
        weight2 = (XBR_WEIGHT*1.75068/10.0/2.0);
    }

    float2 uv = i.uv;
    float dx = In0Size.z;
    float dy = In0Size.w;
    float4 t1 = uv.xyxy + float4(-2.0*dx, -2.0*dy, dx, dy);
    float4 t2 = uv.xyxy + float4(    -dx, -2.0*dy,  0, dy);
    float4 t3 = uv.xyxy + float4(-2.0*dx,     -dy, dx,  0);
    float4 t4 = uv.xyxy + float4(    -dx,     -dy,  0,  0);

    float3 color = XbrCore(wp1, wp2, wp3, wp4, wp5, wp6, weight1, weight2, XBR_EDGE_STR_P2,
        T0.SampleLevel(Pt, t1.xy, 0), T0.SampleLevel(Pt, t1.zy, 0), T0.SampleLevel(Pt, t1.xw, 0), T0.SampleLevel(Pt, t1.zw, 0), // P0 P1 P2 P3
        T0.SampleLevel(Pt, t2.xy, 0), T0.SampleLevel(Pt, t2.zy, 0),                                                             // B C
        T0.SampleLevel(Pt, t3.xy, 0), T0.SampleLevel(Pt, t4.xy, 0), T0.SampleLevel(Pt, t4.zy, 0), T0.SampleLevel(Pt, t3.xw, 0), // D E F G
        T0.SampleLevel(Pt, t4.xw, 0), T0.SampleLevel(Pt, t4.zw, 0),                                                             // H I
        T0.SampleLevel(Pt, t3.zy, 0), T0.SampleLevel(Pt, t3.zw, 0), T0.SampleLevel(Pt, t2.xw, 0), T0.SampleLevel(Pt, t2.zw, 0)); // F4 I4 H5 I5

    return float4(color, 1.0);
}
