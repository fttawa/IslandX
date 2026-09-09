using IslandX.Contracts;

namespace IslandX.Core;

/// <summary>
/// 右键工具栏里的一个视图 —— 点一下，岛体**临时**切到它，指针离开岛体就还原。
///
/// 这不是设置项。设置改的是"以后怎样"，视图问的是"现在这个怎么样"：
/// 想知道雨几时到就点天气，想知道还剩多少电就点电量，看完就走。
///
/// <paramref name="Build"/> 是**临时合成**那条活动的办法，**有它就优先用它** ——
/// 它才是"用户主动问的那个问题"的答案：
///
/// - 媒体、时钟这类**常驻**的，仲裁器里本来就有一条实时的，
///   钉住它即可（歌词照常走字），<c>Build</c> 返回 null。
/// - 天气、电量、Phira 这类**只在事件发生时说话**的，平时没有活动可钉 ——
///   而"未来四小时不下雨""还剩 78%"恰恰是用户点开时想知道的答案，所以要现合成一条。
///   天气尤其明显：它的实时活动是**气象预警**（那个自己会弹），
///   而点开天气想看的是降水时间线 —— 不主动问就看不到的那份。
///
/// <c>Build</c> 返回 null 且该 Provider 也没有实时活动时，按钮置灰 ——
/// 弹一条空的比按钮点不动更糟。
/// </summary>
/// <param name="Glyph">图标码位。全部渲染字形对照表看过，不是凭记忆挑的。</param>
/// <param name="Label">悬停提示。</param>
/// <param name="ProviderId">要钉住哪个 Provider。</param>
/// <param name="Build">临时合成一条活动；没有可看的内容时返回 null。</param>
public sealed record IslandView(
    string Glyph,
    string Label,
    string ProviderId,
    Func<IslandActivity?> Build);
