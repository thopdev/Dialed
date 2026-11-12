using System.Text;
using Bunit;
using Dialed.Client.Backup;
using Dialed.Client.Data;
using Dialed.Domain.Model;
using Dialed.UI.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Dialed.UI.Tests;

public sealed class BackupTests : UiTestContext
{
    private CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private IRepository<T> Store<T>() where T : SyncEntity => Services.GetRequiredService<IRepository<T>>();

    private async Task SeedAsync()
    {
        var vendor = await Store<Vendor>().SaveAsync(new Vendor { Name = "Friedhats" }, Ct);
        var bean = await Store<Bean>().SaveAsync(new Bean { Name = "Guji" }, Ct);
        await Store<EquipmentItem>().SaveAsync(new EquipmentItem { Kind = EquipmentKind.Kettle, Brand = "Fellow" }, Ct);
        await Store<Recipe>().SaveAsync(new Recipe { MethodCode = "v60", Name = "House V60" }, Ct);
        _ = vendor;
        _ = bean;
    }

    [Fact]
    public async Task Planning_a_backup_of_what_is_already_here_adds_nothing()
    {
        await SeedAsync();
        var service = Services.GetRequiredService<BackupService>();
        var file = await service.BuildAsync(Ct);

        var again = await service.PlanAsync(file, Ct);
        again.ShouldNotBeNull();
        again.Total.ShouldBe(0);
        again.AlreadyThere.ShouldBe(4);
    }

    [Fact]
    public async Task Something_that_is_not_a_backup_is_refused()
    {
        var service = Services.GetRequiredService<BackupService>();

        (await service.PlanAsync(Encoding.UTF8.GetBytes("hello"), Ct)).ShouldBeNull();
        (await service.PlanAsync(Encoding.UTF8.GetBytes("{\"format\":\"other\"}"), Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Records_missing_here_are_added_and_unreadable_rows_are_counted()
    {
        var service = Services.GetRequiredService<BackupService>();
        var json = """
        {"format":"dialed-backup","version":1,"tables":{"beans":[{"id":"b1","name":"Huila","photoIds":["p1"]},{"id":"b2"},{"id":"b1","name":"Huila"}]}}
        """;

        var plan = await service.PlanAsync(Encoding.UTF8.GetBytes(json), Ct);

        plan.ShouldNotBeNull();
        plan.New["beans"].ShouldBe(1);
        plan.Unreadable.ShouldBe(1);
        plan.AlreadyThere.ShouldBe(1);
        var result = await service.RestoreAsync(plan, Ct);
        result.Restored.ShouldBe(1);
        var bean = (await Store<Bean>().ListAsync(Ct)).Single();
        bean.Name.ShouldBe("Huila");
        bean.PhotoIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_panel_saves_a_backup_file()
    {
        await SeedAsync();
        var panel = Render<BackupPanel>();

        panel.Find("#backup-export").Click();

        panel.WaitForAssertion(() => panel.Find("#backup-message").TextContent.ShouldContain("saved"));
        var (name, type, content) = ExportedFiles.Saved.Single();
        name.ShouldStartWith("dialed-backup-");
        type.ShouldBe("application/json");
        Encoding.UTF8.GetString(content).ShouldContain("dialed-backup");
    }

    [Fact]
    public void The_panel_previews_and_restores_a_file()
    {
        var panel = Render<BackupPanel>();
        var json = """{"format":"dialed-backup","version":1,"tables":{"vendors":[{"id":"v1","name":"Friedhats"}]}}""";

        panel.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText(json, "backup.json"));

        panel.WaitForAssertion(() => panel.Find("#backup-counts").TextContent.ShouldContain("1 records will be added"));
        panel.Find("#backup-restore").Click();
        panel.WaitForAssertion(() => panel.Find("#backup-message").TextContent.ShouldContain("Restored 1"));
    }

    [Fact]
    public void A_wrong_file_shows_an_error()
    {
        var panel = Render<BackupPanel>();

        panel.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("nope", "x.json"));

        panel.WaitForAssertion(() => panel.Find("#backup-error").TextContent.ShouldContain("not a Dialed backup"));
    }
}
