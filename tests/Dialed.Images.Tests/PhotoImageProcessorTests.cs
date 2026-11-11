using Dialed.Domain.Model;
using SkiaSharp;

namespace Dialed.Images.Tests;

public sealed class PhotoImageProcessorTests
{
    private readonly SkiaPhotoImageProcessor processor = new();

    [Fact]
    public void The_sample_photo_really_carries_GPS_EXIF_XMP_and_a_comment()
    {
        var photo = TestPhotos.WithGps(400, 300);

        JpegMetadata.HasMetadata(photo).ShouldBeTrue();
        JpegMetadata.ReadSegments(photo).Count(s => s.Marker == 0xE1).ShouldBe(2); // EXIF and XMP
        TestPhotos.Contains(photo, "Exif\0\0").ShouldBeTrue();
        TestPhotos.Contains(photo, TestPhotos.LatitudeBytes).ShouldBeTrue();
        TestPhotos.Contains(photo, TestPhotos.Comment).ShouldBeTrue();
    }

    [Fact]
    public void GPS_EXIF_XMP_and_comments_are_gone_from_the_original_and_every_thumbnail()
    {
        var result = processor.Process(TestPhotos.WithGps(1600, 1200), Photo.ThumbnailSizes);

        result.OriginalChanged.ShouldBeTrue();
        foreach (var file in result.Thumbnails.Values.Prepend(result.Original))
        {
            JpegMetadata.HasMetadata(file).ShouldBeFalse();
            JpegMetadata.ReadSegments(file).ShouldNotContain(s => s.Marker == 0xE1);
            TestPhotos.Contains(file, "Exif").ShouldBeFalse();
            TestPhotos.Contains(file, TestPhotos.LatitudeBytes).ShouldBeFalse();
            TestPhotos.Contains(file, TestPhotos.XmpNamespace).ShouldBeFalse();
            TestPhotos.Contains(file, TestPhotos.Comment).ShouldBeFalse();
            TestPhotos.Contains(file, TestPhotos.CameraMake + "\0").ShouldBeFalse();
        }

        TestPhotos.Size(result.Original).ShouldBe((1600, 1200));
    }

    [Theory]
    [InlineData(1600, 1200, 256, 192, 768, 576)]
    [InlineData(1200, 1600, 192, 256, 576, 768)]
    [InlineData(1000, 1000, 256, 256, 768, 768)]
    [InlineData(2048, 512, 256, 64, 768, 192)]
    public void Thumbnails_fit_in_256_and_768_pixels_and_keep_the_aspect_ratio(int width, int height, int smallW, int smallH, int largeW, int largeH)
    {
        var result = processor.Process(TestPhotos.Plain(width, height), Photo.ThumbnailSizes);

        result.Thumbnails.Keys.ShouldBe([256, 768], ignoreOrder: true);
        TestPhotos.Size(result.Thumbnails[256]).ShouldBe((smallW, smallH));
        TestPhotos.Size(result.Thumbnails[768]).ShouldBe((largeW, largeH));
    }

    [Fact]
    public void Small_photos_are_never_scaled_up()
    {
        var result = processor.Process(TestPhotos.Plain(200, 150), Photo.ThumbnailSizes);

        TestPhotos.Size(result.Thumbnails[256]).ShouldBe((200, 150));
        TestPhotos.Size(result.Thumbnails[768]).ShouldBe((200, 150));
    }

    [Fact]
    public void A_clean_photo_is_kept_byte_for_byte()
    {
        var clean = TestPhotos.Plain(800, 600);

        var result = processor.Process(clean, Photo.ThumbnailSizes);

        result.OriginalChanged.ShouldBeFalse();
        result.Original.ShouldBe(clean);
    }

    [Fact]
    public void Photos_larger_than_the_app_sends_are_scaled_down_to_2048()
    {
        var result = processor.Process(TestPhotos.Plain(3000, 1500), Photo.ThumbnailSizes);

        result.OriginalChanged.ShouldBeTrue();
        TestPhotos.Size(result.Original).ShouldBe((2048, 1024));
    }

    [Fact]
    public void The_EXIF_orientation_is_applied_before_it_is_stripped()
    {
        // Stored landscape with "rotate 90° clockwise to view": red (the left half) must end up on top.
        var result = processor.Process(TestPhotos.WithGps(400, 200, orientation: 6), Photo.ThumbnailSizes);

        TestPhotos.Size(result.Original).ShouldBe((200, 400));
        TestPhotos.Size(result.Thumbnails[256]).ShouldBe((128, 256));
        IsRed(TestPhotos.PixelAt(result.Original, 100, 50)).ShouldBeTrue();
        IsRed(TestPhotos.PixelAt(result.Original, 100, 350)).ShouldBeFalse();
    }

    [Fact]
    public void Something_that_is_not_a_JPEG_is_refused_as_invalid()
    {
        Should.Throw<InvalidPhotoException>(() => processor.Process("not an image"u8.ToArray(), Photo.ThumbnailSizes));
        Should.Throw<InvalidPhotoException>(() => processor.Process([0xFF, 0xD8, 0xFF, 0xDB, 0x00, 0x43, 1, 2, 3], Photo.ThumbnailSizes));
    }

    private static bool IsRed(SKColor color) => color.Red > 200 && color.Blue < 60;
}
