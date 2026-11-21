using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.Versioning;
using Serilog;

namespace DiscordSoundboard;

[SupportedOSPlatform("windows")]
public static class IconHelper
{
	private static readonly ILogger Logger = Log.ForContext(typeof(IconHelper));

	private static string GetIconCacheDirectory()
	{
		var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		var dir = Path.Combine(appData, "MacroDeck", "plugins", "com.sertaycil.discord-soundboard", "icons");
		Directory.CreateDirectory(dir);
		return dir;
	}

	public static async Task<byte[]> GetIconPngBytesAsync(DiscordSound sound, HttpClient httpClient, CancellationToken cancellationToken = default)
	{
		var cacheDir = GetIconCacheDirectory();

		// 1. Custom Discord CDN Emoji
		if (!string.IsNullOrEmpty(sound.EmojiId))
		{
			var cdnFilePath = Path.Combine(cacheDir, $"{sound.EmojiId}.png");
			if (File.Exists(cdnFilePath))
			{
				try
				{
					return await File.ReadAllBytesAsync(cdnFilePath, cancellationToken);
				}
				catch { }
			}

			try
			{
				var cdnUrl = $"https://cdn.discordapp.com/emojis/{sound.EmojiId}.png?size=128&quality=lossless";
				var resp = await httpClient.GetAsync(cdnUrl, cancellationToken);
				if (resp.IsSuccessStatusCode)
				{
					var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);
					if (bytes.Length > 0)
					{
						await File.WriteAllBytesAsync(cdnFilePath, bytes, cancellationToken);
						return bytes;
					}
				}
			}
			catch (Exception ex)
			{
				Logger.Warning(ex, "Failed to download emoji icon for {Sound} from CDN", sound.Name);
			}
		}

		// 2. Unicode Emoji
		if (!string.IsNullOrEmpty(sound.EmojiName))
		{
			var hash = Math.Abs(sound.EmojiName.GetHashCode()).ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
			var emojiFilePath = Path.Combine(cacheDir, $"emoji_{hash}.png");
			if (File.Exists(emojiFilePath))
			{
				try
				{
					return await File.ReadAllBytesAsync(emojiFilePath, cancellationToken);
				}
				catch { }
			}

			try
			{
				var bytes = RenderEmojiPng(sound.EmojiName);
				await File.WriteAllBytesAsync(emojiFilePath, bytes, cancellationToken);
				return bytes;
			}
			catch (Exception ex)
			{
				Logger.Warning(ex, "Failed to render emoji icon for {Sound}", sound.Name);
			}
		}

		// 3. Fallback soundboard icon
		var fallbackPath = Path.Combine(cacheDir, "default_sound.png");
		if (File.Exists(fallbackPath))
		{
			try
			{
				return await File.ReadAllBytesAsync(fallbackPath, cancellationToken);
			}
			catch { }
		}

		try
		{
			var bytes = RenderEmojiPng("🔊");
			await File.WriteAllBytesAsync(fallbackPath, bytes, cancellationToken);
			return bytes;
		}
		catch
		{
			return Array.Empty<byte>();
		}
	}

	private static byte[] RenderEmojiPng(string emoji)
	{
		using var bmp = new Bitmap(128, 128, PixelFormat.Format32bppArgb);
		using var g = Graphics.FromImage(bmp);
		g.SmoothingMode = SmoothingMode.AntiAlias;
		g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
		g.InterpolationMode = InterpolationMode.HighQualityBicubic;

		// Discord Blurple Background
		using (var path = CreateRoundedRectanglePath(new Rectangle(4, 4, 120, 120), 24))
		using (var brush = new SolidBrush(Color.FromArgb(88, 101, 242))) // #5865F2
		{
			g.FillPath(brush, path);
		}

		// Emoji text
		using (var font = new Font("Segoe UI Emoji", 50, FontStyle.Regular, GraphicsUnit.Pixel))
		using (var brush = new SolidBrush(Color.White))
		using (var sf = new StringFormat
		{
			Alignment = StringAlignment.Center,
			LineAlignment = StringAlignment.Center
		})
		{
			g.DrawString(emoji, font, brush, new RectangleF(0, 8, 128, 116), sf);
		}

		using var ms = new MemoryStream();
		bmp.Save(ms, ImageFormat.Png);
		return ms.ToArray();
	}

	private static GraphicsPath CreateRoundedRectanglePath(Rectangle rect, int radius)
	{
		var path = new GraphicsPath();
		var diameter = radius * 2;
		path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
		path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
		path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
		path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
		path.CloseFigure();
		return path;
	}
}
