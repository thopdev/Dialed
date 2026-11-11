namespace Dialed.Images.Tests;

public sealed class PhotoBlobPathsTests
{
    [Fact]
    public void Paths_follow_the_architecture_doc_and_round_trip()
    {
        PhotoBlobPaths.Original("h-1", "abc_2").ShouldBe("photos/h-1/abc_2.jpg");
        PhotoBlobPaths.Thumbnail("h-1", "abc_2", 256).ShouldBe("thumbnails/h-1/abc_2-256.jpg");

        PhotoBlobPaths.TryParseOriginal("photos/h-1/abc_2.jpg", out var household, out var photo).ShouldBeTrue();
        (household, photo).ShouldBe(("h-1", "abc_2"));
    }

    [Theory]
    [InlineData("thumbnails/h/p-256.jpg")]
    [InlineData("photos/p.jpg")]
    [InlineData("photos/h/sub/p.jpg")]
    [InlineData("photos/../p.jpg")]
    [InlineData("photos/h/.jpg")]
    [InlineData("photos/h/p.jpeg")]
    [InlineData("photos/h h/p.jpg")]
    public void Anything_else_is_not_an_original(string path)
    {
        PhotoBlobPaths.TryParseOriginal(path, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void Ids_that_could_escape_the_folder_are_refused()
    {
        Should.Throw<ArgumentException>(() => PhotoBlobPaths.Original("..", "p"));
        Should.Throw<ArgumentException>(() => PhotoBlobPaths.ThumbBase("h", "a/b"));
    }
}
