using Bunit;
using Dialed.Client;
using Dialed.Client.Data;
using Dialed.Client.Platform;
using Dialed.Client.Preferences;
using Dialed.Domain.Model;
using Dialed.UI.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class PhotoTests : UiTestContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static CapturedPhoto Jpeg(byte marker = 1) => new([0xFF, 0xD8, marker], "image/jpeg", "photo.jpg");

    private PhotoService Photos => Services.GetRequiredService<PhotoService>();

    private IRepository<Photo> PhotoStore => Services.GetRequiredService<IRepository<Photo>>();

    private ILocalFileStore Files => Services.GetRequiredService<ILocalFileStore>();

    // ---- Service ------------------------------------------------------------------------------

    [Fact]
    public async Task Adding_stores_the_file_and_the_metadata()
    {
        var photo = await Photos.AddAsync(Jpeg(), Ct);

        (await Files.ReadAsync(photo.LocalPath, Ct)).ShouldBe([0xFF, 0xD8, 1]);
        var saved = await PhotoStore.GetAsync(photo.Id, Ct);
        saved.ShouldNotBeNull();
        saved.SizeBytes.ShouldBe(3);
        saved.UploadState.ShouldBe(UploadState.Pending);
        saved.HouseholdId.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Reading_gives_a_data_uri_or_null_when_the_file_is_missing()
    {
        var photo = await Photos.AddAsync(Jpeg(), Ct);

        (await Photos.GetDataUriAsync(photo.Id, Ct))!.ShouldStartWith("data:image/jpeg;base64,");
        (await Photos.GetDataUriAsync("nope", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Removing_deletes_the_file_and_marks_the_record_deleted()
    {
        var photo = await Photos.AddAsync(Jpeg(), Ct);

        await Photos.RemoveAsync(photo.Id, Ct);

        (await Files.ExistsAsync(photo.LocalPath, Ct)).ShouldBeFalse();
        (await PhotoStore.ListAsync(Ct)).ShouldBeEmpty();
    }

    // ---- PhotoStrip ---------------------------------------------------------------------------

    [Fact]
    public void Camera_and_library_buttons_ask_for_the_right_source_and_add_a_photo()
    {
        Camera.Next = Jpeg();
        IReadOnlyList<string> ids = [];
        var strip = Render<PhotoStrip>(p => p
            .Add(x => x.PhotoIds, ids)
            .Add(x => x.PhotoIdsChanged, EventCallback.Factory.Create<IReadOnlyList<string>>(this, v => ids = v)));

        strip.Find("button.photo-camera").Click();

        Camera.LastSource.ShouldBe(PhotoSource.Camera);
        ids.Count.ShouldBe(1);

        strip.Find("button.photo-library").Click();

        Camera.LastSource.ShouldBe(PhotoSource.Library);
    }

    [Fact]
    public void Cancelling_the_camera_adds_nothing()
    {
        Camera.Next = null;
        var changed = false;
        var strip = Render<PhotoStrip>(p => p
            .Add(x => x.PhotoIdsChanged, EventCallback.Factory.Create<IReadOnlyList<string>>(this, _ => changed = true)));

        strip.Find("button.photo-camera").Click();

        changed.ShouldBeFalse();
    }

    [Fact]
    public async Task Existing_photos_show_as_images_and_can_be_removed()
    {
        var photo = await Photos.AddAsync(Jpeg(), Ct);
        IReadOnlyList<string> ids = [photo.Id];
        var strip = Render<PhotoStrip>(p => p
            .Add(x => x.PhotoIds, ids)
            .Add(x => x.PhotoIdsChanged, EventCallback.Factory.Create<IReadOnlyList<string>>(this, v => ids = v)));
        strip.WaitForAssertion(() => strip.FindAll("img").Count.ShouldBe(1));

        strip.Find("button.dialed-photo-remove").Click();

        strip.WaitForAssertion(() => ids.ShouldBeEmpty());
        (await Files.ExistsAsync(photo.LocalPath, Ct)).ShouldBeFalse();
    }

    [Fact]
    public void A_photo_missing_from_this_device_shows_a_placeholder()
    {
        var strip = Render<PhotoStrip>(p => p.Add(x => x.PhotoIds, ["gone"]));

        strip.Markup.ShouldContain("Photo not on this device");
        strip.FindAll("img").ShouldBeEmpty();
    }

    [Fact]
    public void Single_hides_the_add_buttons_once_there_is_a_photo()
    {
        var strip = Render<PhotoStrip>(p => p.Add(x => x.PhotoIds, ["x"]).Add(x => x.Single, true));

        strip.FindAll("button.photo-camera").ShouldBeEmpty();
    }

    [Fact]
    public void Read_only_has_no_buttons()
    {
        var strip = Render<PhotoStrip>(p => p.Add(x => x.PhotoIds, ["x"]).Add(x => x.ReadOnly, true));

        strip.FindAll("button").ShouldBeEmpty();
    }

    // ---- Upload state (#68) -------------------------------------------------------------------

    [Fact]
    public async Task A_new_photo_is_queued_and_shows_it_is_waiting_to_upload()
    {
        var photo = await Photos.AddAsync(Jpeg(), Ct);

        Uploads.StatusOf(photo.Id).ShouldBe(Dialed.Client.Uploads.UploadStatus.Waiting);
        var strip = Render<PhotoStrip>(p => p.Add(x => x.PhotoIds, [photo.Id]));

        strip.Find("[data-upload=waiting]").TextContent.ShouldBe("Waiting to upload");
    }

    [Fact]
    public async Task The_strip_follows_the_queue_and_offers_a_retry_when_an_upload_failed()
    {
        var photo = await Photos.AddAsync(Jpeg(), Ct);
        var strip = Render<PhotoStrip>(p => p.Add(x => x.PhotoIds, [photo.Id]));

        Uploads.Set(photo.Id, Dialed.Client.Uploads.UploadStatus.Uploading);
        strip.WaitForAssertion(() => strip.Find("[data-upload=uploading]").TextContent.ShouldBe("Uploading…"));

        Uploads.Set(photo.Id, Dialed.Client.Uploads.UploadStatus.Failed);
        strip.WaitForAssertion(() => strip.Find("[data-upload=failed]").TextContent.ShouldBe("Upload failed"));
        strip.Find("button.photo-upload-retry").Click();

        Uploads.Retried.ShouldBe([photo.Id]);
        strip.WaitForAssertion(() => strip.FindAll("[data-upload=waiting]").Count.ShouldBe(1));

        Uploads.Set(photo.Id, null);
        strip.WaitForAssertion(() => strip.FindAll("[data-upload]").ShouldBeEmpty());
    }

    [Fact]
    public void Settings_shows_waiting_and_failed_uploads_with_a_retry()
    {
        var status = Render<UploadQueueStatus>();
        status.FindAll("#uploads-pending").ShouldBeEmpty();
        status.FindAll("#uploads-failed").ShouldBeEmpty();

        Uploads.Set("a", Dialed.Client.Uploads.UploadStatus.Waiting);
        Uploads.Set("b", Dialed.Client.Uploads.UploadStatus.Failed);

        status.WaitForAssertion(() => status.Find("#uploads-pending").TextContent.ShouldContain("Photos waiting to upload: 1"));
        status.Find("#uploads-failed").TextContent.ShouldContain("Photos that could not be uploaded: 1");

        status.Find("#uploads-retry").Click();

        Uploads.Retried.ShouldBe(["b"]);
        status.WaitForAssertion(() => status.FindAll("#uploads-failed").ShouldBeEmpty());
        status.Find("#uploads-pending").TextContent.ShouldContain("2");
    }

    // ---- Forms --------------------------------------------------------------------------------

    [Fact]
    public void Bean_form_submits_the_photos_that_were_added()
    {
        Camera.Next = Jpeg();
        Bean? submitted = null;
        var form = Render<BeanForm>(p => p.Add(x => x.OnSubmit, EventCallback.Factory.Create<Bean>(this, b => submitted = b)));
        form.Find("#bean-name").Input("Gesha");
        form.Find("button.photo-camera").Click();

        form.Find("form").Submit();

        submitted.ShouldNotBeNull();
        submitted.PhotoIds.Count.ShouldBe(1);
    }

    [Fact]
    public void Vendor_form_keeps_a_single_logo()
    {
        Camera.Next = Jpeg();
        Vendor? submitted = null;
        var form = Render<VendorForm>(p => p.Add(x => x.OnSubmit, EventCallback.Factory.Create<Vendor>(this, v => submitted = v)));
        form.Find("#vendor-name").Input("Tim Wendelboe");
        form.Find("input[type=checkbox]").Change(true);
        form.Find("button.photo-camera").Click();

        form.Find("form").Submit();

        submitted.ShouldNotBeNull();
        submitted.LogoPhotoId.ShouldNotBeNull();
        form.FindAll("button.photo-camera").ShouldBeEmpty();
    }

    [Fact]
    public void Profile_saves_the_avatar()
    {
        Camera.Next = Jpeg();
        var form = Render<ProfileForm>();
        form.Find("button.photo-camera").Click();

        form.Find("form").Submit();

        form.WaitForAssertion(() => Services.GetRequiredService<IUserPreferences>().Profile.AvatarPhotoId.ShouldNotBeNull());
    }
}
