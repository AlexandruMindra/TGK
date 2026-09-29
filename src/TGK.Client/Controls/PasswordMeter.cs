using System;
using System.Linq;
using SkiaSharp;

namespace TGK.Client.Controls;

/// <summary>Four-segment strength bar with a word ("Too short", "Weak" … "Strong") for new-password fields.</summary>
public sealed class PasswordMeter : Control
{
    /// <summary>Minimum length the UI asks for a new account password.</summary>
    public const int MinLength = 10;

    private static readonly string[] Words = ["Too short", "Weak", "Fair", "Good", "Strong"];
    private string _password = "";

    public string Password { get => _password; set => SetAndPaint(ref _password, value ?? ""); }

    /// <summary>0 = shorter than <see cref="MinLength"/>, then 1 (weak) to 4 (strong), from length and character variety.</summary>
    public static int Score(string password)
    {
        if (password.Length < MinLength)
            return 0;
        int classes = (password.Any(char.IsLower) ? 1 : 0) + (password.Any(char.IsUpper) ? 1 : 0)
            + (password.Any(char.IsDigit) ? 1 : 0) + (password.Any(ch => !char.IsLetterOrDigit(ch)) ? 1 : 0);
        int score = 1 + (password.Length >= 14 ? 1 : 0) + (classes >= 3 ? 1 : 0) + (password.Length >= 20 || classes == 4 ? 1 : 0);
        return Math.Min(4, score);
    }

    protected override void Paint(SKCanvas c)
    {
        if (_password.Length == 0)
        {
            Gfx.Text(c, $"At least {MinLength} characters", 0, H / 2f, Theme.FontXs, Theme.WeightRegular, Theme.TextMuted);
            return;
        }
        int score = Score(_password);
        SKColor color = score switch { 0 or 1 => Theme.Danger, 2 => Theme.Warning, _ => Theme.Success };
        const float segW = 34, gap = 4, barH = 4;
        for (int i = 0; i < 4; i++)
        {
            var r = SKRect.Create(i * (segW + gap), H / 2f - barH / 2f, segW, barH);
            Gfx.FillRound(c, r, 2, i < Math.Max(1, score) ? color : Theme.SurfaceHover);
        }
        Gfx.Text(c, Words[score], 4 * (segW + gap) + 6, H / 2f, Theme.FontXs, Theme.WeightRegular, score == 0 ? Theme.Danger : Theme.TextSecondary);
    }
}
