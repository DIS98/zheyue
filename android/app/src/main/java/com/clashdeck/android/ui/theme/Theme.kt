package com.clashdeck.android.ui.theme

import android.app.Activity
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp
import androidx.core.view.WindowCompat

// 克制的靛蓝 + 青绿，浅色深色各一套
private val Indigo = Color(0xFF5B5BD6)
private val IndigoLight = Color(0xFF9C9CF5)
private val Teal = Color(0xFF12A594)
private val TealLight = Color(0xFF4FD1C5)
private val Danger = Color(0xFFE5484D)

val UpColor = Color(0xFF12A594)
val DownColor = Color(0xFF5B5BD6)

/**
 * 卡片描边，独立于 `outline`。
 *
 * `outline` 要满足输入框「非文本控件 3:1」的下限，直接拿它画卡片会变成硬框；
 * 而把 `outline` 调淡，输入框又看不清。两个用途的对比度要求本来就不同，必须分开：
 * 这里是 1.45:1 左右——足以让卡片边缘从背景里「立」出来，又不会抢内容。
 */
val LocalCardBorder = staticCompositionLocalOf { Color(0xFFD5D5E2) }

private val LightScheme = lightColorScheme(
    primary = Indigo,
    onPrimary = Color.White,
    primaryContainer = Color(0xFFE3E3FB),
    onPrimaryContainer = Color(0xFF1B1B4B),
    secondary = Teal,
    onSecondary = Color.White,
    // 背景压深一档（原 #F7F7FA 与白卡只差 1.07:1，卡片边缘读不出来）
    background = Color(0xFFF2F2F7),
    onBackground = Color(0xFF14141B),
    surface = Color.White,
    onSurface = Color(0xFF14141B),
    // 卡片内的凹槽（分段轨道、进度槽、空态底座）用它：与白卡差 1.14:1 太弱，压深一档
    surfaceVariant = Color(0xFFE9E9F0),
    onSurfaceVariant = Color(0xFF5C5C6B),
    // 输入框与卡片描边用这一档：原 #D8D8E0 对白底只有 1.42:1，深色下更是几乎看不见边框。
    // 3.23:1 是「非文本控件」的可辨下限，同时不会把卡片画成硬框（SectionCard 还叠了 45% 透明度）。
    outline = Color(0xFF8E8E9B),
    outlineVariant = Color(0xFFE8E8F0),
    tertiary = Color(0xFFB65BD6),
    error = Danger,
)

private val DarkScheme = darkColorScheme(
    primary = IndigoLight,
    onPrimary = Color(0xFF14142B),
    primaryContainer = Color(0xFF2A2A5C),
    onPrimaryContainer = Color(0xFFE3E3FB),
    secondary = TealLight,
    onSecondary = Color(0xFF04231F),
    background = Color(0xFF0E0E13),
    onBackground = Color(0xFFEDEDF2),
    // 卡片比背景提亮一档（原 #16161D 与 #0E0E13 只差 1.08:1，深色下几乎分不出卡片边界）
    surface = Color(0xFF1C1C24),
    onSurface = Color(0xFFEDEDF2),
    // 原 #23232C 对卡面 #1C1C24 只有 1.09:1，深色下这些凹槽基本读不出来
    surfaceVariant = Color(0xFF2A2A34),
    onSurfaceVariant = Color(0xFFA0A0B0),
    // 同上，深色底 3.59:1；原 #33333D 对 #16161D 只有 1.45:1。
    outline = Color(0xFF6E6E7C),
    outlineVariant = Color(0xFF26262F),
    tertiary = Color(0xFFC77BE8),
    error = Color(0xFFFF6369),
)

private val AppTypography = Typography(
    // 字号沿用系统可读下限，但把标题的字距收紧一点——中文标题收紧后更像"产品名"而不是"正文加粗"
    headlineSmall = TextStyle(fontSize = 22.sp, lineHeight = 28.sp, fontWeight = FontWeight.SemiBold, letterSpacing = (-0.3).sp),
    titleLarge = TextStyle(fontSize = 20.sp, lineHeight = 26.sp, fontWeight = FontWeight.SemiBold, letterSpacing = (-0.2).sp),
    titleMedium = TextStyle(fontSize = 15.sp, lineHeight = 20.sp, fontWeight = FontWeight.Medium),
    // 正文行高 20sp / 说明 16sp：中文在 1.4 倍行高下才不会挤成一坨
    bodyMedium = TextStyle(fontSize = 14.sp, lineHeight = 20.sp),
    bodySmall = TextStyle(fontSize = 12.sp, lineHeight = 16.sp),
    labelSmall = TextStyle(fontSize = 11.sp, lineHeight = 14.sp, fontWeight = FontWeight.Medium),
)

/** 品牌渐变：图标、首页主按钮、底部选中态共用同一组颜色，避免各处各调一个蓝。 */
val BrandGradientStops: List<Color>
    @Composable get() = listOf(MaterialTheme.colorScheme.primary, MaterialTheme.colorScheme.secondary)

@Composable
fun ClashDeckTheme(
    darkTheme: Boolean = isSystemInDarkTheme(),
    content: @Composable () -> Unit,
) {
    val colors = if (darkTheme) DarkScheme else LightScheme
    val view = LocalContext.current

    SideEffect {
        (view as? Activity)?.window?.let { w ->
            WindowCompat.getInsetsController(w, w.decorView).isAppearanceLightStatusBars = !darkTheme
        }
    }

    CompositionLocalProvider(
        // 卡片描边跟主题走，但不进 ColorScheme（M3 没有这个槽位，塞进 outline 会污染输入框）
        LocalCardBorder provides if (darkTheme) Color(0xFF34343F) else Color(0xFFD5D5E2),
    ) {
        MaterialTheme(colorScheme = colors, typography = AppTypography, content = content)
    }
}
