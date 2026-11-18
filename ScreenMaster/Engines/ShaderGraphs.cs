using System.Collections.Generic;

namespace ScreenMaster.Engines
{
    /// <summary>A pass of a multi-pass upscaler. Scale = output size relative to SOURCE (1 or 2). Inputs: "SOURCE" or an earlier pass's Output name. The last pass writes Output "OUT".</summary>
    public sealed record GraphPass(string Entry, string[] Inputs, string Output, double Scale);

    public sealed record ShaderGraph(string Name, string Resource, GraphPass[] Passes);

    public static class ShaderGraphs
    {
        public static readonly ShaderGraph[] All =
        {
            // Super xBR (Hyllian, MIT). Luma pass folded into pass0/pass1; jinc2 + deblur passes dropped.
            new ShaderGraph("Super xBR", "xbr/super_xbr.hlsl", new[]
            {
                new GraphPass("PS_Xbr_0", new[] { "SOURCE" }, "xbr0", 1),
                new GraphPass("PS_Xbr_1", new[] { "xbr0", "SOURCE" }, "xbr1", 2),
                new GraphPass("PS_Xbr_2", new[] { "xbr1" }, "OUT", 2),
            }),

            // Anime4K v3.2 Upscale CNN x2 (S) (bloc97, MIT).
            new ShaderGraph("Anime4K CNN x2 (S)", "anime4k/anime4k_cnn_x2_s.hlsl", new[]
            {
                new GraphPass("PS_Anime4KS_0", new[] { "SOURCE" }, "conv2d_tf", 1),
                new GraphPass("PS_Anime4KS_1", new[] { "conv2d_tf" }, "conv2d_1_tf", 1),
                new GraphPass("PS_Anime4KS_2", new[] { "conv2d_1_tf" }, "conv2d_2_tf", 1),
                new GraphPass("PS_Anime4KS_3", new[] { "conv2d_2_tf" }, "conv2d_last_tf", 1),
                new GraphPass("PS_Anime4KS_4", new[] { "conv2d_last_tf", "SOURCE" }, "OUT", 2),
            }),

            // Anime4K v3.2 Upscale CNN x2 (M) (bloc97, MIT). The 7-input 1x1 conv is split in two passes.
            new ShaderGraph("Anime4K CNN x2 (M)", "anime4k/anime4k_cnn_x2_m.hlsl", new[]
            {
                new GraphPass("PS_Anime4KM_0", new[] { "SOURCE" }, "conv2d_tf", 1),
                new GraphPass("PS_Anime4KM_1", new[] { "conv2d_tf" }, "conv2d_1_tf", 1),
                new GraphPass("PS_Anime4KM_2", new[] { "conv2d_1_tf" }, "conv2d_2_tf", 1),
                new GraphPass("PS_Anime4KM_3", new[] { "conv2d_2_tf" }, "conv2d_3_tf", 1),
                new GraphPass("PS_Anime4KM_4", new[] { "conv2d_3_tf" }, "conv2d_4_tf", 1),
                new GraphPass("PS_Anime4KM_5", new[] { "conv2d_4_tf" }, "conv2d_5_tf", 1),
                new GraphPass("PS_Anime4KM_6", new[] { "conv2d_5_tf" }, "conv2d_6_tf", 1),
                new GraphPass("PS_Anime4KM_7", new[] { "conv2d_tf", "conv2d_1_tf", "conv2d_2_tf", "conv2d_3_tf" }, "conv2d_last_tf_partA", 1),
                new GraphPass("PS_Anime4KM_8", new[] { "conv2d_last_tf_partA", "conv2d_4_tf", "conv2d_5_tf", "conv2d_6_tf" }, "conv2d_last_tf", 1),
                new GraphPass("PS_Anime4KM_9", new[] { "conv2d_last_tf", "SOURCE" }, "OUT", 2),
            }),
        };
    }
}
