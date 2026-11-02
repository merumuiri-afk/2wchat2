using System.Drawing.Imaging;
using TwoWChat;
using TwoWChat.Core;
using TwoWChat.Images;

internal static partial class Program
{
    private static void CheckGifSticker()
    {
        AppStore.SetDataDirectory(Path.GetFullPath("artifacts/gif-sticker-profile"));
        var bytes = Convert.FromHexString("47494638396101000100800000000000FFFFFF21F904000A0000002C000000000100010000020244010021F904000A0000002C00000000010001000002024C01003B");
        var sticker = StickerLibrary.Prepare(ImageAttachments.Create(bytes, "animation.gif"));
        var path = StickerLibrary.Save(sticker); var loaded = StickerLibrary.Load(path);
        Check(loaded.Info.IsSticker && loaded.Info.ContentType == "image/gif" && loaded.Data.SequenceEqual(bytes) && Path.GetExtension(path) == ".gif", "GIF sticker persists with original bytes and animation format");
        using var animated = ImagePreparation.OpenAnimation(loaded);
        Check(animated.GetFrameCount(FrameDimension.Time) == 2, "GIF sticker retains both animation frames");
        animated.SelectActiveFrame(FrameDimension.Time, 0); using var first = new Bitmap(animated);
        animated.SelectActiveFrame(FrameDimension.Time, 1); using var second = new Bitmap(animated);
        Check(first.GetPixel(0, 0) != second.GetPixel(0, 0), "GIF sticker frames contain different visible pixels");
        (animated.Tag as Stream)?.Dispose();
        var rejected = false; try { StickerLibrary.Prepare(sticker, true); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "GIF background removal is rejected without flattening the animation");
    }
}
