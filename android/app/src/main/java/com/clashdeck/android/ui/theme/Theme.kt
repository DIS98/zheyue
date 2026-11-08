package com.clashdeck.android.ui.theme

import android.app.Activity
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.SideEffect
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

private val LightScheme = lightColorScheme(
    primary = Indigo,
    onPrimary = Color.White,
    primaryContainer = Color(0xFFE3E3FB),
    onPrimaryContainer = Color(0xFF1B1B4B),
    secondary = Teal,
    onSecondary = Color.White,
    background = Color(0xFFF7F7FA),
    onBackground = Color(0xFF14141B),
    surface = Color.White,
    onSurface = Color(0xFF14141B),
    surfaceVariant = Color(0xFFEFEFF4),
    onSurfaceVariant = Color(0xFF5C5C6B),
    outline = Color(0xFFD8D8E0),
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
    surface = Color(0xFF16161D),
    onSurface = Color(0xFFEDEDF2),
    surfaceVariant = Color(0xFF23232C),
    onSurfaceVariant = Color(0xFFA0A0B0),
    outline = Color(0xFF33333D),
    outlineVariant = Color(0xFF26262F),
    tertiary = Color(0xFFC77BE8),
    error = Color(0xFFFF6369),
)

private val AppTypography = Typography(
    // 字号沿用系统可读下限，但把标题的字距收紧一点——中文标题收紧后更像"产品名"而不是"正文加粗"
    headlineSmall = TextStyle(fontSize = 22.sp, fontWeight = FontWeight.SemiBold, letterSpacing = (-0.3).sp),
    titleLarge = TextStyle(fontSize = 20.sp, fontWeight = FontWeight.SemiBold, letterSpacing = (-0.2).sp),
    titleMedium = TextStyle(fontSize = 15.sp, fontWeight = FontWeight.Medium),
    bodyMedium = TextStyle(fontSize = 14.sp),
    bodySmall = TextStyle(fontSize = 12.sp),
    labelSmall = TextStyle(fontSize = 11.sp, fontWeight = FontWeight.Medium),
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

    MaterialTheme(colorScheme = colors, typography = AppTypography, content = content)
}
