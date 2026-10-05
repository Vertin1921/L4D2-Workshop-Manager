using System.Text;
using L4D2ModManager.Core.Models;
using L4D2ModManager.Core.Services;
using L4D2ModManager.Core.Services.Deployment;
using L4D2ModManager.Core.Services.Downloads;
using L4D2ModManager.Core.Services.Mods;
using L4D2ModManager.Core.Services.Steam;
using L4D2ModManager.Core.Services.Vpk;
using L4D2ModManager.Core.Services.Workshop;

namespace L4D2ModManager.Tests;

/// <summary>极简断言工具。</summary>
internal static class Check
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new Exception("断言失败：" + message);
    }

    public static void False(bool condition, string message) => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{message}（期望 {expected}，实际 {actual}）");
    }

    public static void NotNull(object? value, string message)
    {
        if (value == null) throw new Exception("断言失败（对象为 null）：" + message);
    }

    public static void BytesEqual(byte[] expected, byte[]? actual, string message)
    {
        NotNull(actual, message);
        if (expected.Length != actual!.Length)
            throw new Exception($"{message}（长度期望 {expected.Length}，实际 {actual.Length}）");
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
                throw new Exception($"{message}（第 {i} 字节不同：期望 {expected[i]}，实际 {actual[i]}）");
        }
    }
}

internal static class Program
{
    private static readonly List<(string Name, Func<Task> Test)> Tests = new();
    private static string _sandbox = string.Empty;
    private static int _passed;
    private static readonly List<string> Failures = new();

    private static async Task<int> Main()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // 某些终端不支持，忽略
        }

        _sandbox = Path.Combine(AppContext.BaseDirectory, "TestSandbox");
        TryDeleteDirectory(_sandbox);
        Directory.CreateDirectory(_sandbox);

        // 关键：把程序数据目录重定向到沙箱，绝不污染真实的 %AppData%
        Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvironmentVariable, Path.Combine(_sandbox, "data"));
        AppPaths.EnsureCreated();

        Console.WriteLine("=== L4D2 Mod Manager 自检 ===");
        Console.WriteLine($"数据目录：{AppPaths.Root}");
        Console.WriteLine($"沙箱目录：{_sandbox}");
        Console.WriteLine();

        RegisterTests();

        foreach (var (name, test) in Tests)
        {
            try
            {
                await test();
                _passed++;
                Console.WriteLine($"[通过] {name}");
            }
            catch (Exception ex)
            {
                Failures.Add($"{name} → {ex.Message}");
                Console.WriteLine($"[失败] {name}");
                Console.WriteLine($"        {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"=== 结果：{_passed}/{Tests.Count} 通过 ===");
        if (Failures.Count > 0)
        {
            Console.WriteLine("失败列表：");
            foreach (var failure in Failures) Console.WriteLine("  · " + failure);
            return 1;
        }

        Console.WriteLine("全部自检通过。");
        return 0;
    }

    private static void RegisterTests()
    {
        Tests.Add(("VPK 解析：内嵌数据 + 预载数据", TestVpkEmbedded));
        Tests.Add(("VPK 解析：版本 2 + 外部分卷", TestVpkMultiChunk));
        Tests.Add(("VPK 解析：无结尾哨兵的目录树", TestVpkWithoutSentinel));
        Tests.Add(("addoninfo.txt 解析（KeyValues）", TestAddonInfoParse));
        Tests.Add(("Mod 分类推断", TestCategoryClassifier));
        Tests.Add(("扫描 + JSON 数据库读写往返", TestScanAndDatabase));
        Tests.Add(("启用 / 禁用：仅重命名，不损坏文件", TestEnableDisable));
        Tests.Add(("冲突检测：同一文件被多个 Mod 修改", TestConflictDetection));
        Tests.Add(("冲突检测准确度：内容相同不计冲突 / 共享脚本忽略 / 严重程度", TestConflictAccuracy));
        Tests.Add(("搜索 / 过滤 / 排序", TestSearchAndSort));
        Tests.Add(("配置方案：联机模式 / 枪械模式", TestProfiles));
        Tests.Add(("拖放安装 + 删除 Mod", TestInstallAndDelete));
        Tests.Add(("下载落盘：VPK / ZIP 载荷处理", TestDownloadPromotion));
        Tests.Add(("卸载清理：文件删除与延迟清理登记", TestUninstallCleanup));
        Tests.Add(("安装载荷解压（含 zip slip 防护）", TestPayloadExtraction));
        Tests.Add(("仅凭工坊 ID 的下载入队（接口不可用时的降级）", TestDownloadByIdFallback));
        Tests.Add(("安装载荷定位（外置 payload.zip）", TestInstallPayloadLookup));
        Tests.Add(("启动游戏：命令行与 -insecure 参数", TestGameLauncher));
        Tests.Add(("覆盖更新：识别已装目录 + 自动关闭运行中的程序", TestUpdateFlow));
        Tests.Add(("真实安装载荷解压并运行（可选）", TestRealInstallerPayload));
        Tests.Add(("创意工坊链接与 ID 解析", TestWorkshopParsing));
        Tests.Add(("Steam 路径探测（不抛异常）", TestSteamDetection));
        Tests.Add(("真实 VPK 文件解析（可选）", TestRealVpkIfProvided));
        Tests.Add(("真实 VPK 目录批量解析（可选）", TestRealVpkDirectory));
        Tests.Add(("真实 Mod 目录端到端扫描（可选）", TestRealModDirectoryScan));
    }

    // ------------------------------------------------------------------ 测试实现

    private static async Task TestVpkEmbedded()
    {
        await Task.Yield();

        var directory = NewDirectory("vpk-embedded");
        var fileA = Encoding.ASCII.GetBytes(new string('A', 3000));
        var fileB = Encoding.ASCII.GetBytes(new string('B', 500));
        var addonInfo = "\"AddonInfo\"\r\n{\r\n\t\"addontitle\"\t\"Embedded Test\"\r\n\t\"addonauthor\"\t\"Tester\"\r\n}\r\n";

        var writer = new VpkWriter()
            .Add("models/weapons/v_rif_m16.mdl", fileA, preloadBytes: 32)
            .Add("materials/vgui/hud/icon.vtf", fileB)
            .AddText("addoninfo.txt", addonInfo);

        var path = Path.Combine(directory, "Embedded.vpk");
        File.WriteAllBytes(path, writer.Build(1, out _));

        using var archive = VpkReader.Open(path);
        Check.Equal(1u, archive.Version, "版本号应为 1");
        Check.Equal(3, archive.FileCount, "应解析出 3 个文件");
        Check.True(archive.Contains("models/weapons/v_rif_m16.mdl"), "应能按路径找到条目");

        var entry = archive.Find("models/weapons/v_rif_m16.mdl");
        Check.NotNull(entry, "条目不应为空");
        Check.Equal(32, (int)entry!.PreloadBytes, "预载字节数应为 32");
        Check.BytesEqual(fileA, archive.Read(entry), "预载 + 数据区拼装后内容应与原始数据一致");

        Check.BytesEqual(fileB, archive.ReadFile("materials/vgui/hud/icon.vtf"), "普通内嵌文件内容应一致");
        Check.Equal(addonInfo, archive.ReadText("addoninfo.txt"), "addoninfo.txt 文本应一致");

        var info = AddonInfo.FromArchive(archive);
        Check.True(info.Found, "应识别到 addoninfo.txt");
        Check.Equal("Embedded Test", info.Title, "标题应解析正确");
        Check.Equal("Tester", info.Author, "作者应解析正确");
    }

    private static async Task TestVpkMultiChunk()
    {
        await Task.Yield();

        var directory = NewDirectory("vpk-chunked");
        var embedded = Encoding.ASCII.GetBytes(new string('E', 700));
        var external = Encoding.ASCII.GetBytes(new string('X', 4096));

        var writer = new VpkWriter()
            .Add("addoninfo.txt", Encoding.UTF8.GetBytes("\"AddonInfo\" { \"addontitle\" \"Chunked\" }"))
            .Add("models/survivors/survivor_zoey.mdl", embedded)
            .Add("materials/models/survivors/zoey.vtf", external, archiveIndex: 0);

        writer.WriteToDirectory(directory, "chunked", version: 2);
        var dirPath = Path.Combine(directory, "chunked_dir.vpk");
        Check.True(File.Exists(Path.Combine(directory, "chunked_000.vpk")), "应生成外部分卷文件");

        using var archive = VpkReader.Open(dirPath);
        Check.Equal(2u, archive.Version, "版本号应为 2");
        Check.True(archive.IsMultiChunk, "应识别为多分卷 VPK");
        Check.Equal(3, archive.FileCount, "应解析出 3 个文件");
        Check.BytesEqual(external, archive.ReadFile("materials/models/survivors/zoey.vtf"), "外部分卷内容应可读取");
        Check.BytesEqual(embedded, archive.ReadFile("models/survivors/survivor_zoey.mdl"), "内嵌内容应可读取");

        // 扫描器应把 xxx_000.vpk 视为分卷而不是独立 Mod
        Check.False(ModScanner.IsModFile(Path.Combine(directory, "chunked_000.vpk")), "外部分卷不应被当成独立 Mod");
        Check.True(ModScanner.IsModFile(dirPath), "xxx_dir.vpk 应被视为 Mod 主文件");
    }

    private static async Task TestVpkWithoutSentinel()
    {
        await Task.Yield();

        var directory = NewDirectory("vpk-nosentinel");
        var payload = Encoding.ASCII.GetBytes("no-sentinel-payload");

        var writer = new VpkWriter()
            .Add("scripts/vscripts/test.nut", payload)
            .Add("addoninfo.txt", Encoding.UTF8.GetBytes("\"AddonInfo\" { \"addontitle\" \"NoSentinel\" }"));

        var path = Path.Combine(directory, "NoSentinel.vpk");
        File.WriteAllBytes(path, writer.Build(1, out _, writeFinalSentinel: false));

        using var archive = VpkReader.Open(path);
        Check.Equal(2, archive.FileCount, "无结尾哨兵时也应正确解析");
        Check.BytesEqual(payload, archive.ReadFile("scripts/vscripts/test.nut"), "内容应一致");
    }

    private static async Task TestAddonInfoParse()
    {
        await Task.Yield();

        const string standard = @"""AddonInfo""
{
    // 注释行
    ""addonSteamID""      ""123456789""
    ""addontitle""        ""Zenith 武器包""
    ""addonversion""      ""1.5""
    ""addonauthor""       ""TestAuthor""
    ""addondescription""  ""替换 M16 与 AK47 模型\n第二行""
    ""addonurl""          ""https://example.com""
    ""addonTag""          ""weapons""
    ""addonTag""          ""models""
}";

        var info = AddonInfo.Parse(standard);
        Check.True(info.Found, "应解析成功");
        Check.Equal("Zenith 武器包", info.Title, "标题");
        Check.Equal("TestAuthor", info.Author, "作者");
        Check.Equal("1.5", info.Version, "版本");
        Check.Equal("123456789", info.SteamId, "工坊 ID");
        Check.True(info.Description!.Contains("第二行"), "描述中的 \\n 应转换为换行");
        Check.Equal(2, info.Tags.Count, "应收集两个 addonTag");
        Check.Equal("weapons, models", info.TagText, "标签拼接");

        // 容错：无根块 / 大小写混合 / 未加引号的单个词
        var loose = AddonInfo.Parse("addontitle UnquotedTitle\naddonauthor 作者甲\n{ addondescription \"描述\" }");
        Check.Equal("UnquotedTitle", loose.Title, "未加引号的单值也应解析");
        Check.Equal("作者甲", loose.Author, "中文值应解析");

        Check.False(AddonInfo.Parse("").Found, "空文本应返回未找到");
        Check.False(AddonInfo.Parse(null).Found, "null 应返回未找到");
    }

    private static async Task TestCategoryClassifier()
    {
        await Task.Yield();

        var weapon = CategoryClassifier.Classify("m16.vpk", new[]
        {
            "models/weapons/v_rif_m16.mdl",
            "materials/models/weapons/v_rif_m16.vmt",
            "sound/weapons/m16_fire.wav",
        });
        Check.Equal(ModCategory.Weapon, weapon, "武器类路径应识别为武器");

        var character = CategoryClassifier.Classify("zoey.vpk", new[]
        {
            "models/survivors/survivor_zoey.mdl",
            "materials/models/survivors/zoey.vtf",
            "sound/player/zoey/voice.wav",
        });
        Check.Equal(ModCategory.Character, character, "人物模型应识别为人物");

        var map = CategoryClassifier.Classify("campaign.vpk", new[] { "maps/l4d_c1m1.bsp", "missions/mission1.txt" });
        Check.Equal(ModCategory.Map, map, "bsp 应识别为地图");

        var script = CategoryClassifier.Classify("plugin.vpk", new[] { "scripts/vscripts/plugin.nut" });
        Check.Equal(ModCategory.Script, script, "scripts 应识别为脚本");

        var ui = CategoryClassifier.Classify("hud.vpk", new[] { "materials/vgui/hud/health.vtf", "resource/ui_zh.txt" });
        Check.Equal(ModCategory.Ui, ui, "vgui/resource 应识别为 UI");

        var other = CategoryClassifier.Classify("mystery.vpk", new[] { "random/data.bin" });
        Check.Equal(ModCategory.Other, other, "无法识别时应归入其他");

        var byTag = CategoryClassifier.Classify("unknown.vpk", Array.Empty<string>(), "weapons", null);
        Check.Equal(ModCategory.Weapon, byTag, "应能用 addoninfo 标签兜底分类");
    }

    private static async Task TestScanAndDatabase()
    {
        var (service, modDirectory) = CreateLibrary("scan");
        var result = await service.ScanAsync();
        Check.Equal(0, result.Failed, "扫描不应有失败项");

        var mods = service.Mods.ToList();
        Check.Equal(4, mods.Count, "应扫描到 4 个 Mod（分卷文件不计入）");

        var weapon = mods.FirstOrDefault(m => m.DisplayName == "Zenith 武器包");
        Check.NotNull(weapon, "应通过 addoninfo 读取到标题");
        Check.Equal("TestAuthor", weapon!.Author, "作者应来自 addoninfo");
        Check.Equal(ModCategory.Weapon, weapon.Category, "分类应为武器");
        Check.True(weapon.HasAddonInfo, "应标记为存在 addoninfo");
        Check.True(weapon.IsEnabled, "默认应为启用状态");
        Check.True(weapon.SizeBytes > 0, "文件大小应大于 0");
        Check.True(weapon.FileIndex.Count >= 3, "应建立内部文件索引");
        Check.NotNull(weapon.ThumbnailPath, "应从 VPK 中提取到内置缩略图");
        Check.True(File.Exists(weapon.ThumbnailPath), "缩略图文件应存在");

        var noInfo = mods.FirstOrDefault(m => m.BaseName == "ModC");
        Check.NotNull(noInfo, "无 addoninfo 的 Mod 应回退使用文件名");
        Check.Equal("ModC", noInfo!.DisplayName, "显示名应为文件名");
        Check.False(noInfo.HasAddonInfo, "不应标记为存在 addoninfo");
        Check.Equal(ModState.Disabled, noInfo.State, "带 .disabled 后缀的应识别为禁用");
        Check.Equal(ModCategory.Script, noInfo.Category, "脚本 Mod 分类应为脚本");

        var chunked = mods.FirstOrDefault(m => m.BaseName == "chunked_dir");
        Check.NotNull(chunked, "xxx_dir.vpk 应被扫描到");

        // 数据库往返
        Check.True(service.Repository.Save(force: true), "数据库应保存成功");
        Check.True(File.Exists(service.Repository.DatabasePath), "ModDatabase.json 应存在");
        Check.True(File.ReadAllText(service.Repository.DatabasePath).Contains("Zenith 武器包"), "JSON 中应保留中文");

        var reloaded = new ModRepository(service.Repository.DatabasePath);
        reloaded.Load();
        Check.Equal(4, reloaded.Count, "重新加载后记录数应一致");
        var reloadedWeapon = reloaded.FindByWorkshopId("123456789") ?? reloaded.FindByKey(weapon.Key);
        Check.NotNull(reloadedWeapon, "重新加载后应能按 Key 找到记录");
        Check.Equal(ModCategory.Weapon, reloadedWeapon!.Category, "分类应持久化");

        // 二次扫描：文件未变时不应重新解析
        var second = await service.ScanAsync();
        Check.Equal(0, second.Added, "二次扫描不应有新增");
        Check.Equal(4, second.Unchanged, "二次扫描应全部命中缓存");

        _ = modDirectory;
        service.Dispose();
    }

    private static async Task TestEnableDisable()
    {
        var (service, _) = CreateLibrary("state");
        await service.ScanAsync();

        var item = service.Mods.First(m => m.DisplayName == "Zenith 武器包");
        var logical = item.FilePath;
        Check.True(File.Exists(logical), "启用状态下应存在 xxx.vpk");

        // 禁用 → 只重命名
        Check.True(service.SetState(item, ModState.Disabled, out var error), "禁用应成功：" + error);
        Check.True(File.Exists(logical + ".disabled"), "应生成 xxx.vpk.disabled");
        Check.False(File.Exists(logical), "原 xxx.vpk 应已被重命名（不是复制）");
        Check.Equal(ModState.Disabled, item.State, "内存状态应为禁用");
        Check.False(service.Mods.First(m => m.DisplayName == "Zenith 武器包").IsEnabled, "列表中该 Mod 应为禁用");

        // 禁用状态下仍可读取 VPK 内容（文件未被破坏）
        using (var archive = VpkReader.Open(logical + ".disabled"))
        {
            Check.True(archive.FileCount > 0, "禁用后 VPK 仍可解析");
            Check.True(archive.Contains("addoninfo.txt"), "禁用后仍能读到 addoninfo.txt");
        }

        // 启用 → 恢复文件名
        Check.True(service.SetState(item, ModState.Enabled, out error), "启用应成功：" + error);
        Check.True(File.Exists(logical), "应恢复为 xxx.vpk");
        Check.False(File.Exists(logical + ".disabled"), ".disabled 文件应已消失");

        // 批量全部禁用 / 启用（ModC 本来就是禁用状态，应被跳过）
        var disableAll = service.DisableAll();
        Check.Equal(3, disableAll.Changed, "全部禁用应实际改动 3 个");
        Check.Equal(1, disableAll.Skipped, "已禁用的 Mod 应被跳过");
        Check.True(service.Mods.All(m => m.IsDisabled), "全部应为禁用状态");
        Check.True(service.Mods.All(m => File.Exists(m.ActualPath)), "禁用后每个 Mod 的实际路径都应存在");
        Check.False(File.Exists(logical), "禁用后不应再有未加后缀的 .vpk");

        var enableAll = service.EnableAll();
        Check.Equal(4, enableAll.Changed, "全部启用应处理 4 个");
        Check.True(service.Mods.All(m => m.IsEnabled), "全部应为启用状态");

        service.Dispose();
    }

    private static async Task TestConflictDetection()
    {
        var (service, _) = CreateLibrary("conflict");
        await service.ScanAsync();

        var report = await service.DetectConflictsAsync();
        Check.Equal(1, report.FileCount, "应只发现 1 个冲突文件（addoninfo/addonimage 被忽略）");

        var conflict = report.Files[0];
        Check.Equal("models/weapons/v_rif_m16.mdl", conflict.InnerPath, "冲突文件路径应正确");
        Check.Equal(2, conflict.Count, "应涉及 2 个 Mod");
        Check.True(conflict.IsActiveConflict, "两者都启用时应为活动冲突");

        var names = conflict.Mods.Select(m => m.ModName).OrderBy(n => n).ToList();
        Check.True(names.Contains("Zenith 武器包"), "应包含武器 Mod");
        Check.True(names.Contains("Survivor Skin"), "应包含人物 Mod");

        var weapon = service.Mods.First(m => m.DisplayName == "Zenith 武器包");
        Check.Equal(1, weapon.ConflictCount, "武器 Mod 应记录 1 个冲突");

        // 禁用其中一个后不再是“活动冲突”
        service.SetState(weapon, ModState.Disabled, out _);
        var report2 = await service.DetectConflictsAsync();
        Check.Equal(1, report2.FileCount, "仍然存在潜在冲突");
        Check.Equal(0, report2.ActiveConflictCount, "两者不同时启用时不应是活动冲突");

        service.Dispose();
    }

    /// <summary>
    /// 冲突准确度：
    ///   · 同一路径、CRC 相同 → 内容一致 → 不算冲突（IdenticalDuplicate，默认隐藏）；
    ///   · 同一路径、CRC 不同 → 真实冲突，并按目录给出严重程度；
    ///   · 内置忽略的共享脚本库 → 完全不参与比较；
    ///   · 自定义忽略关键字 → 可以把"人人都带"的公共文件排除掉。
    /// </summary>
    private static async Task TestConflictAccuracy()
    {
        var modDirectory = NewDirectory("conflict-accuracy");

        var weaponBytes = Encoding.ASCII.GetBytes(new string('W', 4096));
        var weaponOtherBytes = Encoding.ASCII.GetBytes(new string('X', 4096));
        var sameVmtBytes = Encoding.ASCII.GetBytes(new string('V', 512));   // 两个 Mod 里完全一致
        var libraryBytesA = Encoding.ASCII.GetBytes("director base A");
        var libraryBytesB = Encoding.ASCII.GetBytes("director base B");
        var pluginBytesA = Encoding.ASCII.GetBytes("plugin A");
        var pluginBytesB = Encoding.ASCII.GetBytes("plugin B");

        var infoA = "\"AddonInfo\" { \"addontitle\" \"ModA\" }";
        var infoB = "\"AddonInfo\" { \"addontitle\" \"ModB\" }";

        // ModA / ModB：同一模型文件但内容不同（真实冲突）；同一共享脚本（内置忽略）；同一脚本文件但内容不同（中危）
        // ModC：与 ModA 有完全相同的材质文件（CRC 相同 → 不算冲突）
        var modA = new VpkWriter()
            .AddText("addoninfo.txt", infoA)
            .Add("models/weapons/v_rif_m16.mdl", weaponBytes)
            .Add("materials/models/weapons/v_rif_m16.vmt", sameVmtBytes)
            .Add("scripts/vscripts/director_base_addon.nut", libraryBytesA)
            .Add("scripts/vscripts/custom_plugin.nut", pluginBytesA);

        var modB = new VpkWriter()
            .AddText("addoninfo.txt", infoB)
            .Add("models/weapons/v_rif_m16.mdl", weaponOtherBytes)
            .Add("scripts/vscripts/director_base_addon.nut", libraryBytesB)
            .Add("scripts/vscripts/custom_plugin.nut", pluginBytesB);

        var modC = new VpkWriter()
            .AddText("addoninfo.txt", "\"AddonInfo\" { \"addontitle\" \"ModC\" }")
            .Add("materials/models/weapons/v_rif_m16.vmt", sameVmtBytes);

        File.WriteAllBytes(Path.Combine(modDirectory, "AccuracyA.vpk"), modA.Build(1, out _));
        File.WriteAllBytes(Path.Combine(modDirectory, "AccuracyB.vpk"), modB.Build(1, out _));
        File.WriteAllBytes(Path.Combine(modDirectory, "AccuracyC.vpk"), modC.Build(1, out _));

        var service = new ModLibraryService(
            Path.Combine(AppPaths.Root, "config-accuracy.json"),
            Path.Combine(AppPaths.Root, "ModDatabase-accuracy.json"));
        service.Initialize();
        service.Config.AutoDetectSteamPaths = false;
        service.Config.ModDirectories.Clear();
        service.AddDirectory(modDirectory);

        await service.ScanAsync();
        var report = await service.DetectConflictsAsync();

        var weapon = report.Files.FirstOrDefault(f => f.InnerPath.Contains("v_rif_m16.mdl"));
        Check.NotNull(weapon, "内容不同的模型文件应被识别为冲突");
        Check.False(weapon!.ContentIdentical, "内容不同的文件不应标记为内容相同");
        Check.Equal(ConflictSeverity.High, weapon.Severity, "模型文件应为高严重程度");
        Check.True(weapon.IsActiveConflict, "两个都启用时应为活动冲突");

        var vmt = report.Files.Where(f => f.InnerPath.Contains("v_rif_m16.vmt")).ToList();
        Check.True(vmt.Count > 0 && vmt.All(f => f.ContentIdentical), "CRC 相同的重复文件应全部标记为内容相同");
        Check.True(report.IdenticalDuplicateCount >= 1, "应统计出内容相同的重复文件数量");

        var plugin = report.Files.FirstOrDefault(f => f.InnerPath.Contains("custom_plugin.nut"));
        Check.NotNull(plugin, "内容不同的脚本文件应被识别为冲突");
        Check.Equal(ConflictSeverity.Medium, plugin!.Severity, "脚本应为中严重程度");

        Check.False(report.Files.Any(f => f.InnerPath.Contains("director_base_addon.nut")),
            "内置忽略的共享脚本库不应出现在冲突列表中");
        Check.False(report.Files.Any(f => f.InnerPath.Contains("addoninfo.txt")), "addoninfo 不应算冲突");

        // 每个 Mod 的冲突计数只统计真实冲突（ModA：模型 + 脚本 = 2）
        var itemA = service.Mods.First(m => m.DisplayName == "ModA");
        Check.Equal(2, itemA.ConflictCount, "ModA 应记录 2 个真实冲突");

        Check.True(report.SummaryText.Contains("真实冲突"), "摘要应使用「真实冲突」措辞：" + report.SummaryText);
        Check.True(report.HighSeverityCount >= 1, "应统计出高严重程度冲突数量");

        // 自定义忽略关键字：把模型冲突排除后该文件不再出现
        service.Config.ConflictIgnorePatterns = new List<string> { "models/weapons/v_rif_m16.mdl" };
        var filtered = await service.DetectConflictsAsync();
        Check.False(filtered.Files.Any(f => f.InnerPath.Contains("v_rif_m16.mdl")), "自定义忽略关键字应能排除该文件");
        Check.True(filtered.IgnoredFiles > report.IgnoredFiles, "忽略文件计数应增加");

        service.Dispose();
    }

    private static async Task TestSearchAndSort()
    {
        var (service, _) = CreateLibrary("query");
        await service.ScanAsync();
        var mods = service.Mods.ToList();

        var byName = new ModQuery { SearchText = "Zenith" }.Apply(mods);
        Check.Equal(1, byName.Count, "按名称搜索应命中 1 个");

        var byAuthor = new ModQuery { SearchText = "TestAuthor" }.Apply(mods);
        Check.Equal(1, byAuthor.Count, "按作者搜索应命中 1 个");

        var byCategory = new ModQuery { SearchText = "脚本" }.Apply(mods);
        Check.True(byCategory.Count >= 1, "按分类中文名搜索应命中");

        var byCategoryFilter = new ModQuery { Category = ModCategory.Script }.Apply(mods);
        Check.Equal(1, byCategoryFilter.Count, "按分类过滤应命中脚本 Mod");

        var enabledOnly = new ModQuery { Enabled = true }.Apply(mods);
        Check.Equal(3, enabledOnly.Count, "应过滤出 3 个已启用 Mod");

        var disabledOnly = new ModQuery { Enabled = false }.Apply(mods);
        Check.Equal(1, disabledOnly.Count, "应过滤出 1 个已禁用 Mod");

        // 默认排序：禁用优先
        var defaultOrder = new ModQuery { SortMode = ModSortMode.Default, DisabledFirst = true }.Apply(mods);
        Check.True(defaultOrder[0].IsDisabled, "默认排序应把禁用的 Mod 排在最前");

        var bySizeDesc = new ModQuery { SortMode = ModSortMode.SizeDesc }.Apply(mods);
        Check.True(bySizeDesc[0].SizeBytes >= bySizeDesc[^1].SizeBytes, "按大小降序应生效");

        var byNameDesc = new ModQuery { SortMode = ModSortMode.NameDesc }.Apply(mods);
        Check.True(string.Compare(byNameDesc[0].DisplayName, byNameDesc[^1].DisplayName, StringComparison.CurrentCultureIgnoreCase) >= 0,
            "名称 Z-A 排序应生效");

        var recent = new ModQuery { SortMode = ModSortMode.RecentlyAdded }.Apply(mods);
        Check.True(recent[0].AddedUtc >= recent[^1].AddedUtc, "最近添加排序应生效");

        Check.True(ModQuery.DescribeSortMode(ModSortMode.Default).Contains("禁用"), "排序描述应可读");

        service.Dispose();
    }

    private static async Task TestProfiles()
    {
        var (service, _) = CreateLibrary("profiles");
        await service.ScanAsync();
        await service.DetectConflictsAsync();

        service.EnableAll();

        var online = service.Config.Profiles.First(p => p.Preset == ProfilePreset.OnlineSafe);
        var result = service.ApplyProfile(online);

        var script = service.Mods.First(m => m.Category == ModCategory.Script);
        Check.True(script.IsDisabled, "联机模式应禁用脚本 Mod");

        var map = service.Mods.First(m => m.Category == ModCategory.Map);
        Check.True(map.IsEnabled, "联机模式应保留地图以外的非脚本 Mod（地图本身启用）");

        var weaponMod = service.Mods.First(m => m.DisplayName == "Zenith 武器包");
        var survivorMod = service.Mods.First(m => m.DisplayName == "Survivor Skin");
        Check.True(weaponMod.IsDisabled && survivorMod.IsDisabled, "联机模式应禁用互相冲突的 Mod");
        Check.Equal(0, result.Failed, "应用方案不应有失败");

        var weaponsOnly = service.Config.Profiles.First(p => p.Preset == ProfilePreset.WeaponsOnly);
        service.ApplyProfile(weaponsOnly);
        Check.True(service.Mods.First(m => m.DisplayName == "Zenith 武器包").IsEnabled, "枪械模式应启用武器 Mod");
        Check.True(service.Mods.First(m => m.DisplayName == "Survivor Skin").IsDisabled, "枪械模式应禁用人物 Mod");
        Check.True(service.Mods.First(m => m.Category == ModCategory.Map).IsDisabled, "枪械模式应禁用地国 Mod");

        // 自定义方案：捕获当前状态后再应用
        var captured = service.SaveCurrentAsProfile("我的方案", "测试用");
        Check.Equal(ProfilePreset.Custom, captured.Preset, "捕获的方案应为自定义");
        Check.True(captured.Overrides.Count == 4, "应记录 4 个 Mod 的状态");

        service.EnableAll();
        service.ApplyProfile(captured);
        Check.True(service.Mods.First(m => m.DisplayName == "Zenith 武器包").IsEnabled, "自定义方案应恢复武器 Mod 为启用");
        Check.True(service.Mods.First(m => m.Category == ModCategory.Script).IsDisabled, "自定义方案应恢复脚本 Mod 为禁用");

        // 内置方案不可删除，自定义方案可删除
        Check.False(service.DeleteProfile(online), "内置方案不应被删除");
        Check.True(service.DeleteProfile(captured), "自定义方案应可删除");

        service.Dispose();
    }

    private static async Task TestInstallAndDelete()
    {
        var (service, modDirectory) = CreateLibrary("install");
        await service.ScanAsync();

        // 模拟拖放：在别处生成一个 .vpk，然后安装
        var sourceDirectory = NewDirectory("install-source");
        var sourcePath = Path.Combine(sourceDirectory, "DroppedMod.vpk");
        var writer = new VpkWriter()
            .AddText("addoninfo.txt", "\"AddonInfo\" { \"addontitle\" \"拖放安装测试\" \"addonauthor\" \"Dropper\" }")
            .Add("maps/l4d_drop.bsp", Encoding.ASCII.GetBytes(new string('M', 4096)));
        File.WriteAllBytes(sourcePath, writer.Build(1, out _));

        var (item, message) = await service.InstallVpkAsync(sourcePath, modDirectory);
        Check.NotNull(item, "安装应返回 Mod 条目：" + message);
        Check.Equal("拖放安装测试", item!.DisplayName, "应读取到安装后的 addoninfo");
        Check.Equal(ModCategory.Map, item.Category, "分类应为地图");
        Check.True(File.Exists(Path.Combine(modDirectory, "DroppedMod.vpk")), "文件应被复制到 Mod 目录");
        Check.True(service.Mods.Any(m => m.DisplayName == "拖放安装测试"), "应加入数据库");
        Check.Equal(5, service.Mods.Count, "列表应为 5 个 Mod");

        // 删除：文件 + 记录 + 缩略图
        var thumbnailBefore = item.ThumbnailPath;
        var deleteResult = service.DeleteMod(item);
        Check.True(deleteResult.Success, "删除应成功：" + deleteResult.Error);
        Check.False(File.Exists(Path.Combine(modDirectory, "DroppedMod.vpk")), "VPK 文件应被删除");
        Check.False(service.Mods.Any(m => m.DisplayName == "拖放安装测试"), "数据库记录应被删除");
        if (thumbnailBefore != null) Check.False(File.Exists(thumbnailBefore), "缩略图缓存应被删除");

        // 安装非 VPK 文件应被拒绝
        var badPath = Path.Combine(sourceDirectory, "notamod.txt");
        File.WriteAllText(badPath, "hello");
        var (badItem, badMessage) = await service.InstallVpkAsync(badPath, modDirectory);
        Check.NotNull(badMessage, "应返回提示信息");
        Check.True(badItem == null, "非 VPK 文件不应被安装");

        // 目录管理
        Check.True(service.AddDirectory(modDirectory) is false, "重复添加同一目录应被拒绝");
        var extraDirectory = NewDirectory("install-extra");
        Check.True(service.AddDirectory(extraDirectory), "添加新目录应成功");
        Check.True(service.Config.ModDirectories.Contains(extraDirectory), "配置中应包含新目录");
        Check.True(service.RemoveDirectory(extraDirectory), "移除目录应成功");
        Check.False(service.Config.ModDirectories.Contains(extraDirectory), "配置中不应再包含该目录");

        service.Dispose();
    }

    /// <summary>下载落盘：验证 VPK / ZIP 载荷的识别、移动、解压与重名处理。</summary>
    private static async Task TestDownloadPromotion()
    {
        await Task.Yield();

        var directory = NewDirectory("download-promote");
        var vpkBytes = new VpkWriter()
            .AddText("addoninfo.txt", "\"AddonInfo\" { \"addontitle\" \"下载测试\" \"addonauthor\" \"Downloader\" }")
            .Add("maps/l4d_dl.bsp", Encoding.ASCII.GetBytes(new string('D', 2048)))
            .Build(1, out _);

        // 1) VPK 载荷
        var partPath = Path.Combine(directory, "123456789.part");
        File.WriteAllBytes(partPath, vpkBytes);

        Check.True(DownloadFileHelper.IsVpkFile(partPath), "应识别为 VPK 文件");
        Check.False(DownloadFileHelper.IsZipFile(partPath), "VPK 不应被识别为 ZIP");

        var info = new WorkshopItemInfo
        {
            PublishedFileId = "123456789",
            FileName = "123456789.vpk",
            FileSize = vpkBytes.Length,
        };
        var task = new DownloadTask
        {
            WorkshopId = "123456789",
            DestinationDirectory = Path.Combine(directory, "out"),
            Title = "下载测试",
        };

        var finalPath = DownloadFileHelper.PromoteFile(partPath, task, info);
        Check.True(File.Exists(finalPath), "VPK 应被移动到目标目录：" + finalPath);
        Check.Equal("123456789.vpk", Path.GetFileName(finalPath), "应使用工坊原始文件名");
        Check.BytesEqual(vpkBytes, File.ReadAllBytes(finalPath), "移动后内容应保持一致");
        Check.False(File.Exists(partPath), "临时 .part 文件应已被移走");

        // 2) ZIP 载荷（内部含 .vpk）
        var zipPath = Path.Combine(directory, "987654321.part");
        using (var archive = new System.IO.Compression.ZipArchive(File.Create(zipPath),
                   System.IO.Compression.ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("MyAddon.vpk");
            using var stream = entry.Open();
            stream.Write(vpkBytes);
        }

        Check.True(DownloadFileHelper.IsZipFile(zipPath), "应识别为 ZIP 文件");

        var info2 = new WorkshopItemInfo { PublishedFileId = "987654321", FileName = "987654321.zip" };
        var task2 = new DownloadTask
        {
            WorkshopId = "987654321",
            DestinationDirectory = Path.Combine(directory, "out2"),
            Title = "ZIP 测试",
        };

        var final2 = DownloadFileHelper.PromoteFile(zipPath, task2, info2);
        Check.True(File.Exists(final2), "ZIP 内的 VPK 应被解压出来");
        Check.Equal("MyAddon.vpk", Path.GetFileName(final2), "解压后的文件名应正确");
        Check.BytesEqual(vpkBytes, File.ReadAllBytes(final2), "解压后内容应保持一致");

        // 3) 目标目录已存在同名文件时自动改名，绝不覆盖
        var third = Path.Combine(directory, "654321.part");
        File.WriteAllBytes(third, vpkBytes);
        var task3 = new DownloadTask
        {
            WorkshopId = "123456789",
            DestinationDirectory = Path.Combine(directory, "out"),
            Title = "重名测试",
        };

        var final3 = DownloadFileHelper.PromoteFile(third, task3, info);
        Check.True(!string.Equals(final3, finalPath, StringComparison.OrdinalIgnoreCase), "同名文件不应被覆盖");
        Check.True(File.Exists(finalPath), "原有文件应仍然存在");
        Check.True(File.Exists(final3), "新文件应使用新的文件名");
    }

    /// <summary>卸载清理：验证纯 .NET 的删除流程（不使用 cmd 脚本），占用文件会被登记为重启后删除。</summary>
    private static async Task TestUninstallCleanup()
    {
        await Task.Yield();

        var directory = NewDirectory("uninstall-cleanup");
        Directory.CreateDirectory(Path.Combine(directory, "runtimes", "win-x64", "native"));
        Directory.CreateDirectory(Path.Combine(directory, "zh-Hans"));

        for (int i = 0; i < 12; i++)
            File.WriteAllText(Path.Combine(directory, $"file{i}.dll"), new string('x', 256));

        File.WriteAllText(Path.Combine(directory, "runtimes", "win-x64", "native", "WebView2Loader.dll"), "native");
        File.WriteAllText(Path.Combine(directory, "zh-Hans", "L4D2ModManager.resources.dll"), "satellite");

        Check.True(Directory.Exists(directory), "测试目录应已创建");

        var outcome = AppDeployment.DeleteDirectoryFiles(directory, TimeSpan.FromSeconds(1));

        Check.True(outcome.DeletedFiles >= 14, $"应删除至少 14 个文件（实际 {outcome.DeletedFiles}）");
        Check.False(Directory.Exists(directory), "所有文件与空目录都应被删除，包括安装目录本身");
        Check.True(outcome.FullyRemoved, "没有文件被占用时不应有延迟清理项：" + outcome.Summary);

        // 占用中的文件：这里用一个仍打开的句柄模拟（Windows 上以 FileShare.None 打开即无法删除）
        var lockedDirectory = NewDirectory("uninstall-locked");
        var lockedFile = Path.Combine(lockedDirectory, "locked.dll");
        File.WriteAllText(lockedFile, "locked");

        using (var handle = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var lockedOutcome = AppDeployment.DeleteDirectoryFiles(lockedDirectory, TimeSpan.FromMilliseconds(600));
            Check.True(lockedOutcome.DeferredFiles >= 1, "被占用的文件应被计入延迟清理项");
        }

        // 句柄释放后目录应可删除（模拟重启后由系统清理）
        try
        {
            File.Delete(lockedFile);
            Directory.Delete(lockedDirectory, recursive: true);
        }
        catch
        {
            // 即使失败也不影响测试结论：真实场景由系统在重启时清理
        }
    }

    /// <summary>安装载荷解压：验证解压到目标目录、保留子目录结构、拒绝 zip slip、载荷缺失时报错。</summary>
    private static async Task TestPayloadExtraction()
    {
        var work = NewDirectory("payload-extract");
        var zipPath = Path.Combine(work, "payload.zip");

        using (var archive = new System.IO.Compression.ZipArchive(File.Create(zipPath),
                   System.IO.Compression.ZipArchiveMode.Create))
        {
            WriteZipEntry(archive, "L4D2ModManager.exe", "fake-exe");
            WriteZipEntry(archive, "L4D2ModManager.Core.dll", new string('c', 4096));
            WriteZipEntry(archive, "runtimes/win-x64/native/WebView2Loader.dll", "native");
            WriteZipEntry(archive, "Assets/app.ico", "icon");
            WriteZipEntry(archive, "../escaped.txt", "must-not-be-written");
        }

        var target = Path.Combine(work, "installed");
        var extracted = await AppDeployment.ExtractPayloadAsync(zipPath, target);

        Check.Equal(4, extracted, "应解压 4 个文件（zip slip 条目应被跳过）");
        Check.True(File.Exists(Path.Combine(target, "L4D2ModManager.exe")), "根目录文件应被解压");
        Check.True(File.Exists(Path.Combine(target, "runtimes", "win-x64", "native", "WebView2Loader.dll")),
            "子目录结构应被保留");
        Check.True(File.Exists(Path.Combine(target, "Assets", "app.ico")), "Assets 目录应被创建");
        Check.False(File.Exists(Path.Combine(work, "escaped.txt")), "指向目标目录之外的条目不应被解压");

        // 载荷缺失时应明确报错，而不是静默成功
        var missing = await Task.Run(async () =>
        {
            try
            {
                await AppDeployment.ExtractPayloadAsync(Path.Combine(work, "not-found.zip"), target);
                return false;
            }
            catch (FileNotFoundException)
            {
                return true;
            }
        });

        Check.True(missing, "载荷文件不存在时应抛出 FileNotFoundException");

        await Task.Yield();
    }

    private static void WriteZipEntry(System.IO.Compression.ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = new StreamWriter(entry.Open(), Encoding.UTF8);
        stream.Write(content);
    }

    /// <summary>
    /// 接口不可用（被墙 / 限流）时，仅凭工坊 ID 也应能入队，
    /// 并给出"改用订阅缓存 / steamcmd"的提示，而不是直接报"缺少创意工坊信息"。
    /// </summary>
    private static async Task TestDownloadByIdFallback()
    {
        var config = AppConfig.CreateDefault();
        var work = NewDirectory("download-by-id");
        config.DownloadDirectory = work;

        using var workshop = new SteamWorkshopClient(() => config.SteamWebApiKey);
        var steamPaths = new Core.Services.Steam.SteamPaths();
        using var manager = new DownloadManager(workshop, () => config, () => steamPaths);

        var task = manager.EnqueueById("123456789", work, null, WorkshopItemInfo.BuildPageUrl("123456789"));
        Check.Equal("123456789", task.WorkshopId, "任务应记录工坊 ID");

        // 等待任务进入终态（三通道在本机都不可用时应为失败，但错误信息必须说明通道而非缺少信息）
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (task.IsFinished is false && DateTime.UtcNow < deadline)
            await Task.Delay(200).ConfigureAwait(false);

        Check.True(task.IsFinished, "任务应在超时前进入终态");
        Check.True(task.Status is DownloadStatus.Failed or DownloadStatus.Completed,
            $"状态应为失败或完成，实际 {task.Status}");

        if (task.Status == DownloadStatus.Failed)
        {
            var error = task.Error ?? string.Empty;
            Check.False(error.Contains("缺少创意工坊信息", StringComparison.Ordinal),
                "接口不可用时不应因为缺少信息而直接失败：" + error);
            Check.True(error.Contains("下载通道", StringComparison.Ordinal) ||
                       error.Contains("订阅缓存", StringComparison.Ordinal) ||
                       error.Contains("steamcmd", StringComparison.Ordinal),
                "错误信息应说明可用的下载通道：" + error);
        }
    }

    /// <summary>
    /// 可选：设置环境变量 L4D2MM_TEST_PAYLOAD 指向真实的 payload.zip，
    /// 验证"解压 → 得到可运行程序 → 运行 --version"这一整条安装链路（不需要管理员权限）。
    /// </summary>
    private static async Task TestRealInstallerPayload()
    {
        var payload = Environment.GetEnvironmentVariable("L4D2MM_TEST_PAYLOAD");
        if (string.IsNullOrWhiteSpace(payload) || !File.Exists(payload))
        {
            Console.WriteLine("        （未提供 L4D2MM_TEST_PAYLOAD，跳过真实安装载荷验证）");
            return;
        }

        var directory = NewDirectory("real-payload");
        var sizeMb = Math.Round(new FileInfo(payload).Length / 1024.0 / 1024.0, 1);
        var extracted = await AppDeployment.ExtractPayloadAsync(payload, directory);

        Check.True(extracted > 100, $"应解压出多个文件（实际 {extracted}）");
        Check.True(File.Exists(Path.Combine(directory, "L4D2ModManager.exe")), "应包含主程序");
        Check.True(File.Exists(Path.Combine(directory, "L4D2ModManager.Core.dll")), "应包含核心库");
        Check.True(File.Exists(Path.Combine(directory, "runtimes", "win-x64", "native", "WebView2Loader.dll")),
            "应包含 WebView2 原生库");
        Check.True(File.Exists(Path.Combine(directory, "app.manifest")) ||
                   File.Exists(Path.Combine(directory, "L4D2ModManager.runtimeconfig.json")),
            "应包含运行时配置文件");

        // 真正把解压出来的程序跑起来（无界面模式），确认载荷是完整可执行的
        var dataDirectory = Path.Combine(directory, "..", "real-payload-data");
        var outputFile = Path.Combine(directory, "..", "real-payload-out.txt");
        var startInfo = new System.Diagnostics.ProcessStartInfo(Path.Combine(directory, "L4D2ModManager.exe"), "--version")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory,
        };
        startInfo.Environment["L4D2MM_DATA_DIR"] = Path.GetFullPath(dataDirectory);

        using var process = System.Diagnostics.Process.Start(startInfo);
        Check.NotNull(process, "应能启动解压后的主程序");

        var stdout = await process!.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        File.WriteAllText(outputFile, stdout);

        Check.Equal(0, process.ExitCode, "解压后的主程序 --version 应正常退出");
        Check.True(stdout.Contains("Left 4 Dead 2 Mod Manager"), "应输出版本信息：" + stdout.Trim());

        Console.WriteLine($"        载荷 {sizeMb} MB → 解压 {extracted} 个文件，解压后的主程序可正常运行");
    }

    /// <summary>安装载荷定位：验证"Setup.exe 同级 / payload 子目录 / 上一级"三种摆放方式都能找到载荷。</summary>
    private static async Task TestInstallPayloadLookup()
    {
        await Task.Yield();

        var work = NewDirectory("payload-lookup");

        // 1) 同级目录
        var sameLevel = Path.Combine(work, "same-level");
        Directory.CreateDirectory(sameLevel);
        File.WriteAllText(Path.Combine(sameLevel, InstallPayload.FileName), "zip");
        Check.Equal(Path.Combine(sameLevel, InstallPayload.FileName),
            InstallPayload.Find(sameLevel) ?? string.Empty, "应能找到同级的 payload.zip");

        // 2) payload 子目录
        var withSub = Path.Combine(work, "with-sub");
        Directory.CreateDirectory(Path.Combine(withSub, "payload"));
        File.WriteAllText(Path.Combine(withSub, "payload", InstallPayload.FileName), "zip");
        Check.True(InstallPayload.Find(withSub) != null, "应能找到 payload 子目录下的载荷");

        // 3) 上一级目录（artifacts\Setup\ 与 artifacts\payload.zip 并列的常见摆放）
        var parent = Path.Combine(work, "parent");
        var child = Path.Combine(parent, "Setup");
        Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(parent, InstallPayload.FileName), "zip");
        Check.True(InstallPayload.Find(child) != null, "应能找到上一级目录的载荷");

        // 4) 都不存在时应返回 null，而不是抛异常
        var empty = Path.Combine(work, "empty");
        Directory.CreateDirectory(empty);
        Check.True(InstallPayload.Find(empty, empty) == null, "没有载荷时应返回 null");
        Check.False(InstallPayload.IsAvailable(empty), "IsAvailable 应为 false");
        Check.Equal(0L, InstallPayload.GetSize(empty), "载荷不存在时大小应为 0");
    }

    /// <summary>启动游戏：命令行组装（含 -insecure）、游戏可执行文件定位。</summary>
    private static async Task TestGameLauncher()
    {
        await Task.Yield();

        // 1) 参数组装
        var normal = new GameLaunchOptions();
        Check.Equal("-applaunch 550", normal.ToSteamArguments(), "正常启动应通过 Steam 启动（无附加参数）");

        var insecure = new GameLaunchOptions { Insecure = true };
        Check.Equal("-applaunch 550 -insecure", insecure.ToSteamArguments(), "-insecure 应附在 Steam 启动参数后");
        Check.Equal("-insecure", insecure.ToDirectArguments(), "直接启动时应只有 -insecure");
        Check.True(insecure.Describe().Contains("VAC"), "说明里应提醒已关闭 VAC");

        var combined = new GameLaunchOptions { Insecure = true, Console = true, ExtraArguments = "-windowed \"-novid test\"" };
        var parts = combined.ToArgumentList();
        Check.True(parts.Contains("-insecure") && parts.Contains("-console") && parts.Contains("-windowed"),
            "-insecure/-console/-windowed 都应出现：" + string.Join(' ', parts));
        Check.True(parts.Contains("-novid test"), "引号包裹的参数应保持为一段：" + string.Join(' ', parts));

        // 2) 用假的 Steam 目录树验证可执行文件定位
        var root = NewDirectory("launcher");
        var steamRoot = Path.Combine(root, "Steam");
        var gameDir = Path.Combine(steamRoot, "steamapps", "common", "Left 4 Dead 2");
        var addonDir = Path.Combine(gameDir, "left4dead2", "addons");
        Directory.CreateDirectory(addonDir);
        File.WriteAllText(Path.Combine(steamRoot, "steam.exe"), "stub");
        File.WriteAllText(Path.Combine(gameDir, "left4dead2.exe"), "stub");

        var paths = new SteamPaths
        {
            SteamRoot = steamRoot,
            Libraries = new List<string> { Path.Combine(steamRoot, "steamapps") },
            AddonDirectories = new List<string> { addonDir },
            GameDirectories = new List<string> { gameDir },
        };

        Check.Equal(Path.Combine(gameDir, "left4dead2.exe"), GameLauncher.FindGameExecutable(paths) ?? string.Empty,
            "应能找到 left4dead2.exe");
        Check.Equal(Path.Combine(steamRoot, "steam.exe"), GameLauncher.FindSteamExecutable(paths) ?? string.Empty,
            "应能找到 steam.exe");

        var plan = GameLauncher.Build(paths, new GameLaunchOptions { Insecure = true, DryRun = true });
        Check.True(plan.Success, "应能生成启动命令");
        Check.True(plan.UsedSteam, "本机有 steam.exe 时应优先通过 Steam 启动");
        Check.True(plan.CommandLine.EndsWith("-applaunch 550 -insecure", StringComparison.Ordinal), "命令行：" + plan.CommandLine);

        // DryRun 不应真的启动进程
        var dry = GameLauncher.Launch(paths, new GameLaunchOptions { Insecure = true, DryRun = true });
        Check.True(dry.Success && dry.UsedSteam, "DryRun 应返回计划而不启动进程");

        // 3) 找不到游戏时应给出可读错误
        var empty = new SteamPaths { SteamRoot = Path.Combine(root, "NoSteam") };
        var missing = GameLauncher.Build(empty, new GameLaunchOptions { DryRun = true });
        Check.False(missing.Success, "找不到游戏时不应报告成功");
        Check.True((missing.Error ?? string.Empty).Contains("Left 4 Dead 2"), "错误信息应说明没找到游戏：" + missing.Error);
    }

    /// <summary>
    /// 覆盖更新流程：
    ///   · 关闭不存在的进程不应报错；
    ///   · 注册表安装信息能往返（安装器据此自动填入已装目录）——本机已有安装记录时跳过，避免破坏它；
    ///   · 真正启动一个同名进程，验证能被自动关闭。
    /// </summary>
    private static async Task TestUpdateFlow()
    {
        // 1) 不存在的进程
        var none = AppDeployment.CloseRunningInstances("l4d2mm-not-running-stub");
        Check.Equal(0, none.Closed, "没有该进程时关闭数应为 0");
        Check.Equal(0, none.Remaining, "没有该进程时残留数应为 0");

        // 2) 注册表往返（保护本机已有记录）
        if (AppDeployment.TryGetInstalledInfo() != null)
        {
            Console.WriteLine("        （本机已存在安装记录，跳过注册表往返测试以保护它）");
        }
        else
        {
            var installDirectory = NewDirectory("update-install");
            AppDeployment.WriteUninstallRegistry(installDirectory, 1024 * 1024);

            var info = AppDeployment.TryGetInstalledInfo();
            Check.NotNull(info, "应能从注册表读回安装信息");
            Check.Equal(installDirectory, info!.Location, "读回的安装目录应与写入一致");

            AppDeployment.RemoveUninstallRegistry();
            Check.True(AppDeployment.TryGetInstalledInfo() == null, "删除注册表后应读不到安装信息");
        }

        // 3) 真进程：复制一份 cmd.exe 改名为被测进程名
        var stubDirectory = NewDirectory("update-stub");
        var stubPath = Path.Combine(stubDirectory, "L4D2MMStub.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), stubPath, overwrite: true);

        var startInfo = new System.Diagnostics.ProcessStartInfo(stubPath, "/c ping -n 60 127.0.0.1 > nul")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        };

        using var stub = System.Diagnostics.Process.Start(startInfo);
        if (stub == null)
        {
            Console.WriteLine("        （无法启动测试进程，跳过自动关闭验证）");
            return;
        }

        await Task.Delay(800).ConfigureAwait(false);

        var closed = AppDeployment.CloseRunningInstances("L4D2MMStub", gracePeriodMs: 800);
        Check.True(closed.Closed >= 1, $"应至少关闭 1 个实例（实际 {closed.Closed}）");
        Check.Equal(0, closed.Remaining, "关闭后不应还有残留实例");

        // 4) 默认进程名应来自主程序可执行文件名
        var defaultName = AppDeployment.CloseRunningInstances();
        Check.True(defaultName.Remaining >= 0, "默认进程名调用不应抛异常");
    }

    private static async Task TestWorkshopParsing()
    {
        await Task.Yield();

        Check.Equal("123456789", SteamWorkshopClient.ParseWorkshopId("https://steamcommunity.com/sharedfiles/filedetails/?id=123456789"),
            "标准分享链接");
        Check.Equal("987654321", SteamWorkshopClient.ParseWorkshopId("https://steamcommunity.com/sharedfiles/filedetails/?id=987654321&searchtext=x"),
            "带额外参数的链接");
        Check.Equal("555555555", SteamWorkshopClient.ParseWorkshopId("steam://url/CommunityFilePage/555555555"), "steam 协议链接");
        Check.Equal("222222222", SteamWorkshopClient.ParseWorkshopId("222222222"), "纯数字 ID");
        Check.Equal("333333333", SteamWorkshopClient.ParseWorkshopId("  https://steamcommunity.com/workshop/filedetails/?id=333333333  "),
            "带空格的链接");
        Check.True(SteamWorkshopClient.ParseWorkshopId("这不是链接") == null, "无效输入应返回 null");
        Check.True(SteamWorkshopClient.ParseWorkshopId("") == null, "空输入应返回 null");

        Check.True(SteamWorkshopClient.IsWorkshopReference("https://steamcommunity.com/sharedfiles/filedetails/?id=123456789"),
            "应识别为工坊引用");
        Check.True(AppInfo.BuildSearchUrl("zoey").Contains("searchtext=zoey"), "搜索地址应包含关键字");
        Check.True(AppInfo.BuildSearchUrl("手电筒").Contains("%E6%89%8B"), "中文关键字应被编码");
        Check.True(AppInfo.BuildSearchUrl(null, "trend", 3).Contains("p=3"), "分页参数应生效");
    }

    private static async Task TestSteamDetection()
    {
        await Task.Yield();

        var paths = SteamLibraryLocator.Detect();
        Check.NotNull(paths, "探测结果不应为 null");
        Check.NotNull(paths.Libraries, "库列表不应为 null");
        Console.WriteLine($"        （本机探测：Steam 根目录={paths.SteamRoot ?? "未安装"}，库数={paths.Libraries.Count}，" +
                          $"addons 目录={paths.AddonDirectories.Count}，工坊目录={paths.WorkshopDirectories.Count}）");
    }

    /// <summary>可选：设置环境变量 L4D2MM_TEST_VPK 指向一个真实 VPK，用真实文件验证解析器。</summary>
    private static async Task TestRealVpkIfProvided()
    {
        await Task.Yield();

        var path = Environment.GetEnvironmentVariable("L4D2MM_TEST_VPK");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Console.WriteLine("        （未提供 L4D2MM_TEST_VPK，跳过真实 VPK 验证）");
            return;
        }

        using var archive = VpkReader.Open(path);
        Check.True(archive.FileCount > 0, "真实 VPK 应解析出文件");
        Console.WriteLine($"        真实 VPK：{Path.GetFileName(path)} → {archive.FileCount} 个文件，版本 {archive.Version}，" +
                          $"多分卷={archive.IsMultiChunk}");

        var addonInfo = AddonInfo.FromArchive(archive);
        if (addonInfo.Found)
            Console.WriteLine($"        addoninfo：标题=\"{addonInfo.Title}\"，作者=\"{addonInfo.Author}\"，标签=\"{addonInfo.TagText}\"");
        else
            Console.WriteLine("        addoninfo：未找到");

        // 随机抽取若干文件验证内容可读
        var sample = archive.Entries.Where(e => e.TotalLength > 0 && e.TotalLength < 4 * 1024 * 1024).Take(5).ToList();
        foreach (var entry in sample)
        {
            var bytes = archive.Read(entry);
            Check.NotNull(bytes, $"应能读取 {entry.FullPath}");
            Check.Equal((int)entry.TotalLength, bytes!.Length, $"{entry.FullPath} 读取长度应等于条目长度");
        }
    }

    /// <summary>可选：设置环境变量 L4D2MM_TEST_VPK_DIR 指向真实 Mod 目录，批量验证解析器健壮性。</summary>
    private static async Task TestRealVpkDirectory()
    {
        await Task.Yield();

        var directory = Environment.GetEnvironmentVariable("L4D2MM_TEST_VPK_DIR");
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            Console.WriteLine("        （未提供 L4D2MM_TEST_VPK_DIR，跳过批量真实 VPK 验证）");
            return;
        }

        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(ModScanner.IsModFile)
            .ToList();

        Check.True(files.Count > 0, "目录中应存在 .vpk 文件");

        int ok = 0, failed = 0, totalEntries = 0, withAddonInfo = 0, lengthMismatch = 0, withImage = 0;
        var parseErrors = new List<string>();

        foreach (var file in files)
        {
            try
            {
                using var archive = VpkReader.Open(file);
                if (archive.FileCount == 0) throw new Exception("解析出 0 个文件");

                totalEntries += archive.FileCount;
                if (AddonInfo.FromArchive(archive).Found) withAddonInfo++;
                if (archive.FindAddonImage() != null) withImage++;

                foreach (var entry in archive.Entries.Where(e => e.TotalLength > 0).Take(3))
                {
                    var bytes = archive.Read(entry);
                    if (bytes == null || bytes.Length != entry.TotalLength) lengthMismatch++;
                }

                ok++;
            }
            catch (Exception ex)
            {
                failed++;
                parseErrors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        Console.WriteLine($"        真实 VPK 共 {files.Count} 个：成功 {ok}，失败 {failed}；" +
                          $"内部文件合计 {totalEntries:N0}；含 addoninfo {withAddonInfo} 个；含内置图片 {withImage} 个；长度不符 {lengthMismatch}");

        foreach (var error in parseErrors.Take(5)) Console.WriteLine("        [失败] " + error);

        Check.Equal(0, lengthMismatch, "抽样读取长度应全部与条目长度一致");
        Check.True(failed == 0, $"所有真实 VPK 都应能解析（失败 {failed} 个）");
    }

    /// <summary>可选：对真实 Mod 目录执行完整扫描 + 冲突检测，验证端到端流程与性能。</summary>
    private static async Task TestRealModDirectoryScan()
    {
        var directory = Environment.GetEnvironmentVariable("L4D2MM_TEST_VPK_DIR");
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            Console.WriteLine("        （未提供 L4D2MM_TEST_VPK_DIR，跳过端到端扫描）");
            return;
        }

        var service = new ModLibraryService(
            Path.Combine(AppPaths.Root, "config-real.json"),
            Path.Combine(AppPaths.Root, "ModDatabase-real.json"));
        service.Initialize();
        service.Config.AutoDetectSteamPaths = false;
        service.Config.ModDirectories.Clear();
        service.AddDirectory(directory);

        var scan = await service.ScanAsync();
        Console.WriteLine($"        {scan.Summary}");
        Check.True(scan.FilesFound > 0, "应扫描到 Mod 文件");
        Check.Equal(0, scan.Failed, "扫描不应出现失败项");

        var mods = service.Mods.ToList();
        Check.Equal(scan.FilesFound, mods.Count, "数据库条目数应与发现的文件数一致");
        Check.True(mods.Count(m => m.FileIndex.Count > 0) > 0, "应建立内部文件索引");

        var withTitle = mods.Count(m => m.HasAddonInfo && !string.IsNullOrWhiteSpace(m.DisplayName));
        Console.WriteLine($"        含 addoninfo 的 Mod：{withTitle}/{mods.Count}；分类分布：" +
                          string.Join("，", mods.GroupBy(m => m.Category)
                              .OrderByDescending(g => g.Count())
                              .Select(g => $"{ModCategoryInfo.Full(g.Key)}={g.Count()}")));

        var report = await service.DetectConflictsAsync();
        Console.WriteLine($"        {report.SummaryText}");
        if (report.FileCount > 0)
        {
            Console.WriteLine($"        示例冲突：{report.Files[0].InnerPath} ← {report.Files[0].ModsText}");
        }

        // 二次扫描应命中缓存，明显更快
        var second = await service.ScanAsync();
        Check.Equal(mods.Count, second.Unchanged, "二次扫描应全部命中缓存");

        service.Dispose();
    }

    // ------------------------------------------------------------------ 辅助
    /// <summary>创建一个使用沙箱目录的 Mod 库，并生成测试用 VPK 文件。</summary>
    private static (ModLibraryService Service, string ModDirectory) CreateLibrary(string name)
    {
        var modDirectory = NewDirectory("mods-" + name);

        // 1) 武器 Mod（含 addoninfo、缩略图、与 ModB 冲突的模型）
        var weaponInfo = "\"AddonInfo\"\r\n{\r\n" +
                         "\t\"addonSteamID\"\t\t\"123456789\"\r\n" +
                         "\t\"addontitle\"\t\t\"Zenith 武器包\"\r\n" +
                         "\t\"addonauthor\"\t\t\"TestAuthor\"\r\n" +
                         "\t\"addondescription\"\t\"替换 M16 模型\"\r\n" +
                         "\t\"addonversion\"\t\t\"1.0\"\r\n" +
                         "\t\"addonTag\"\t\t\t\"weapons\"\r\n" +
                         "}\r\n";

        var weaponVpk = new VpkWriter()
            .AddText("addoninfo.txt", weaponInfo)
            .Add("addonimage.jpg", FakeJpeg())
            .Add("models/weapons/v_rif_m16.mdl", Encoding.ASCII.GetBytes(new string('W', 8192)))
            .Add("materials/models/weapons/v_rif_m16.vmt", Encoding.ASCII.GetBytes(new string('w', 1024)))
            .Add("sound/weapons/m16_fire.wav", Encoding.ASCII.GetBytes(new string('s', 512)));

        File.WriteAllBytes(Path.Combine(modDirectory, "ModA.vpk"), weaponVpk.Build(1, out _));

        // 2) 人物 Mod（与武器 Mod 冲突：同一个 v_rif_m16.mdl）
        var survivorInfo = "\"AddonInfo\" { \"addontitle\" \"Survivor Skin\" \"addonauthor\" \"SkinArtist\" \"addonTag\" \"character\" }";
        var survivorVpk = new VpkWriter()
            .AddText("addoninfo.txt", survivorInfo)
            .Add("models/survivors/survivor_zoey.mdl", Encoding.ASCII.GetBytes(new string('Z', 4096)))
            .Add("materials/models/survivors/zoey.vtf", Encoding.ASCII.GetBytes(new string('z', 512)))
            .Add("sound/player/zoey/voice_laugh.wav", Encoding.ASCII.GetBytes(new string('v', 256)))
            // 故意与 ModA 的同一文件写入不同内容：这才是真正的"覆盖冲突"（内容相同会被判为无影响）
            .Add("models/weapons/v_rif_m16.mdl", Encoding.ASCII.GetBytes(new string('Z', 8192)));

        File.WriteAllBytes(Path.Combine(modDirectory, "ModB.vpk"), survivorVpk.Build(1, out _));

        // 3) 无 addoninfo 的脚本 Mod，且处于禁用状态
        var scriptVpk = new VpkWriter()
            .Add("scripts/vscripts/plugin.nut", Encoding.ASCII.GetBytes("// script"))
            .Add("scripts/plugin.txt", Encoding.ASCII.GetBytes("cfg"));

        File.WriteAllBytes(Path.Combine(modDirectory, "ModC.vpk.disabled"), scriptVpk.Build(1, out _));

        // 4) 多分卷地图 Mod（xxx_dir.vpk + xxx_000.vpk）
        var mapWriter = new VpkWriter()
            .AddText("addoninfo.txt", "\"AddonInfo\" { \"addontitle\" \"Chunked 地图\" }")
            .Add("maps/l4d_test.bsp", Encoding.ASCII.GetBytes(new string('B', 2048)))
            .Add("materials/maps/l4d_test.vtf", Encoding.ASCII.GetBytes(new string('T', 6000)), archiveIndex: 0);
        mapWriter.WriteToDirectory(modDirectory, "chunked", version: 1);

        var service = new ModLibraryService(
            Path.Combine(AppPaths.Root, $"config-{name}.json"),
            Path.Combine(AppPaths.Root, $"ModDatabase-{name}.json"));

        service.Initialize();
        // 自检必须完全隔离：不要自动引入本机 Steam 目录
        service.Config.AutoDetectSteamPaths = false;
        service.Config.ModDirectories.Clear();
        service.Config.Profiles = ModProfile.CreateBuiltIns();
        service.AddDirectory(modDirectory);

        return (service, modDirectory);
    }

    private static byte[] FakeJpeg()
    {
        var bytes = new byte[256];
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;
        bytes[3] = 0xE0;
        for (int i = 4; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);
        return bytes;
    }

    private static string NewDirectory(string name)
    {
        var path = Path.Combine(_sandbox, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 忽略
        }
    }
}
