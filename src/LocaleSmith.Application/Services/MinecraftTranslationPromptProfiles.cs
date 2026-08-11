using LocaleSmith.Core.Models;

namespace LocaleSmith.Application.Services;

internal static class MinecraftTranslationPromptProfiles
{
    private const string UnknownInstructions = """
        Specialist profile: minecraft-java-generic.
        The package type could not be determined safely. Use the path and translation key only as
        contextual hints, and prefer established Minecraft terminology over assumptions about a mod,
        resource pack, or shader pack.
        """;

    private const string ModInstructions = """
        Specialist profile: minecraft-java-mod.
        You localize Minecraft Java mods: loader metadata, mod language resources, configuration screens,
        gameplay systems, items, blocks, entities, recipes, advancements, and verified user-facing strings
        externalized from Java classes.
        Preserve mod and project names, namespaces, loader names, commands, configuration keys, API names,
        protocol names, and branded technology terms unless an established localization is explicit.
        Distinguish vanilla Minecraft concepts from mod-defined mechanics. For ambiguous words such as
        power, level, charge, or tank, use the visible sentence and translation key to choose the gameplay
        meaning; never force a glossary entry merely because the English spelling matches.
        """;

    private const string ModZhCnGlossary = """
        Preferred professional terminology table for Simplified Chinese (English => preferred zh-CN):
        mod => 模组
        mod loader => 模组加载器
        dependency => 依赖
        configuration / config => 配置
        item => 物品
        block => 方块
        entity => 实体
        mob => 生物
        biome => 生物群系
        dimension => 维度
        enchantment => 魔咒
        status effect => 状态效果
        recipe => 配方
        inventory => 物品栏
        slot => 槽位
        advancement => 进度
        loot table => 战利品表
        cooldown => 冷却时间
        redstone => 红石
        experience points / XP => 经验值
        """;

    private const string ResourcePackInstructions = """
        Specialist profile: minecraft-java-resource-pack.
        You localize Minecraft Java resource packs: pack descriptions, language resources, UI labels, fonts,
        sounds, particles, models, block states, atlases, textures, and animation metadata.
        Preserve pack names, author names, asset namespaces, resource locations, file names, translation keys,
        sound-event identifiers, and model or texture identifiers. Translate only user-visible prose.
        Distinguish the current resource-pack concept from the historical texture-pack concept. Do not inject
        mod-loader or shader-rendering vocabulary unless the source text genuinely describes that feature.
        Keep short menu labels compact and keep descriptions natural rather than expanding them into tutorials.
        """;

    private const string ResourcePackZhCnGlossary = """
        Preferred professional terminology table for Simplified Chinese (English => preferred zh-CN):
        resource pack => 资源包
        texture pack => 材质包
        texture => 纹理
        texture atlas => 纹理图集
        sprite => 精灵图
        model => 模型
        block state => 方块状态
        user interface / UI => 用户界面
        GUI => 图形用户界面
        font => 字体
        glyph => 字形
        sound event => 声音事件
        particle => 粒子
        colormap => 颜色映射
        animation => 动画
        animation metadata => 动画元数据
        emissive texture => 自发光纹理
        resource location => 资源位置
        """;

    private const string ShaderPackInstructions = """
        Specialist profile: minecraft-java-shader-pack.
        You localize OptiFine- and Iris-compatible Minecraft Java shader packs, especially option labels,
        profiles, screens, comments, compatibility notices, and graphics-quality tooltips.
        Preserve shader option identifiers, macro and #define names, uniforms, buffer and program names,
        file paths, OpenGL/GLSL terms, GPU or vendor names, API names, and established abbreviations such as
        TAA, SSAO, POM, SSR, HDR, and LUT. Translate the user-visible expansion or explanation around them.
        Distinguish rendering terms precisely: exposure is not brightness, bloom is not generic glow, and
        ambient occlusion is not a shadow-quality setting. Keep option labels concise and make performance or
        compatibility warnings technically explicit without inventing hardware guarantees.
        """;

    private const string ShaderPackZhCnGlossary = """
        Preferred professional terminology table for Simplified Chinese (English => preferred zh-CN):
        shader pack => 光影包
        shader => 着色器
        vertex shader => 顶点着色器
        fragment shader => 片段着色器
        shadow => 阴影
        ambient occlusion => 环境光遮蔽
        screen-space ambient occlusion / SSAO => 屏幕空间环境光遮蔽（SSAO）
        global illumination => 全局光照
        volumetric lighting => 体积光照
        bloom => 泛光
        tone mapping => 色调映射
        exposure => 曝光
        gamma => 伽马
        depth of field / DOF => 景深（DOF）
        motion blur => 动态模糊
        anti-aliasing => 抗锯齿
        temporal anti-aliasing / TAA => 时间抗锯齿（TAA）
        normal map => 法线贴图
        specular map => 高光贴图
        parallax occlusion mapping / POM => 视差遮蔽映射（POM）
        screen-space reflection / SSR => 屏幕空间反射（SSR）
        color grading => 色彩分级
        render scale => 渲染比例
        """;

    public static string Create(
        MinecraftContentKind contentKind,
        TranslationLanguage targetLanguage)
    {
        ArgumentNullException.ThrowIfNull(targetLanguage);
        var profile = contentKind switch
        {
            MinecraftContentKind.Mod => new PromptProfile(ModInstructions, ModZhCnGlossary),
            MinecraftContentKind.ResourcePack => new PromptProfile(
                ResourcePackInstructions,
                ResourcePackZhCnGlossary),
            MinecraftContentKind.ShaderPack => new PromptProfile(
                ShaderPackInstructions,
                ShaderPackZhCnGlossary),
            MinecraftContentKind.Unknown => new PromptProfile(UnknownInstructions, null),
            _ => throw new ArgumentOutOfRangeException(
                nameof(contentKind),
                contentKind,
                "The Minecraft content kind is not supported.")
        };

        return $"{profile.Instructions}{Environment.NewLine}{CreateTerminologyGuidance(profile, targetLanguage)}";
    }

    private static string CreateTerminologyGuidance(
        PromptProfile profile,
        TranslationLanguage targetLanguage)
    {
        if (string.Equals(targetLanguage.CanonicalLocale, "zh_CN", StringComparison.Ordinal) &&
            profile.SimplifiedChineseGlossary is not null)
        {
            return $"""
                {profile.SimplifiedChineseGlossary}
                Apply a preferred mapping only when its domain sense matches the source. Keep established
                proper names intact and use one chosen term consistently across the batch.
                """;
        }

        return $"""
            Terminology policy for {targetLanguage.PromptLanguageName}:
            Use the official Minecraft localization and established target-language professional terminology
            for this specialist domain. Do not copy terms from a glossary for another locale.
            When no official term exists, choose the established player or graphics-community term and keep it
            consistent across the batch.
            """;
    }

    private sealed record PromptProfile(
        string Instructions,
        string? SimplifiedChineseGlossary);
}
