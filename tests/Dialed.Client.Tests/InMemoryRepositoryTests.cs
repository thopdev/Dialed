using Dialed.Client.Data;
using Dialed.Domain.Model;

namespace Dialed.Client.Tests;

public sealed class InMemoryRepositoryTests
{
    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class Context(string user, string household) : ICurrentContext
    {
        public string UserId => user;

        public string HouseholdId => household;
    }

    private static readonly DateTimeOffset Start = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTime time = new(Start);

    private InMemoryRepository<Vendor> Create(string user = "u1", string household = "h1") =>
        new(new Context(user, household), time);

    [Fact]
    public async Task Saving_a_new_record_stamps_household_user_time_and_version()
    {
        var repository = Create();

        var saved = await repository.SaveAsync(new Vendor { Name = "Friedhats" });

        saved.HouseholdId.ShouldBe("h1");
        saved.CreatedBy.ShouldBe("u1");
        saved.UpdatedBy.ShouldBe("u1");
        saved.CreatedAt.ShouldBe(Start);
        saved.UpdatedAt.ShouldBe(Start);
        saved.Version.ShouldBe(1);
        saved.Deleted.ShouldBeFalse();
        Guid.TryParse(saved.Id, out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Saving_a_change_raises_the_version_and_keeps_creation_details()
    {
        var repository = Create();
        var created = await repository.SaveAsync(new Vendor { Name = "Friedhats" });
        time.Advance(TimeSpan.FromMinutes(5));

        var changed = await repository.SaveAsync(created with { Name = "Friedhats Coffee", CreatedBy = "forged", HouseholdId = "forged" });

        changed.Name.ShouldBe("Friedhats Coffee");
        changed.Version.ShouldBe(2);
        changed.CreatedBy.ShouldBe("u1");
        changed.HouseholdId.ShouldBe("h1");
        changed.CreatedAt.ShouldBe(Start);
        changed.UpdatedAt.ShouldBe(Start.AddMinutes(5));
        (await repository.GetAsync(created.Id))!.Name.ShouldBe("Friedhats Coffee");
    }

    [Fact]
    public async Task Saving_from_an_outdated_copy_is_a_conflict()
    {
        var repository = Create();
        var first = await repository.SaveAsync(new Vendor { Name = "A" });
        await repository.SaveAsync(first with { Name = "B" });

        var conflict = await Should.ThrowAsync<ConflictException>(() => repository.SaveAsync(first with { Name = "C" }));

        conflict.Id.ShouldBe(first.Id);
        (await repository.GetAsync(first.Id))!.Name.ShouldBe("B");
    }

    [Fact]
    public async Task Deleted_records_disappear_from_get_and_list()
    {
        var repository = Create();
        var keep = await repository.SaveAsync(new Vendor { Name = "Keep" });
        var remove = await repository.SaveAsync(new Vendor { Name = "Remove" });

        await repository.DeleteAsync(remove.Id);

        (await repository.GetAsync(remove.Id)).ShouldBeNull();
        (await repository.ListAsync()).Select(vendor => vendor.Id).ShouldBe([keep.Id]);
    }

    [Fact]
    public async Task Deleting_twice_or_a_missing_record_is_not_an_error_and_notifies_once()
    {
        var repository = Create();
        var vendor = await repository.SaveAsync(new Vendor { Name = "A" });
        var changes = 0;
        repository.Changed += () => changes++;

        await repository.DeleteAsync(vendor.Id);
        await repository.DeleteAsync(vendor.Id);
        await repository.DeleteAsync("does-not-exist");

        changes.ShouldBe(1);
    }

    [Fact]
    public async Task Getting_a_missing_record_returns_null()
    {
        (await Create().GetAsync("nope")).ShouldBeNull();
    }

    [Fact]
    public async Task Changes_are_announced_on_save()
    {
        var repository = Create();
        var changes = 0;
        repository.Changed += () => changes++;

        var saved = await repository.SaveAsync(new Vendor { Name = "A" });
        await repository.SaveAsync(saved with { Name = "B" });

        changes.ShouldBe(2);
    }

    [Fact]
    public async Task Null_entities_are_rejected()
    {
        await Should.ThrowAsync<ArgumentNullException>(() => Create().SaveAsync(null!));
    }

    [Fact]
    public void Local_context_has_a_user_and_household()
    {
        var context = new LocalContext();

        context.UserId.ShouldNotBeNullOrEmpty();
        context.HouseholdId.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Every_entity_type_can_be_stored()
    {
        var context = new Context("u1", "h1");
        var bean = await new InMemoryRepository<Bean>(context, time).SaveAsync(new Bean { Name = "Gesha" });
        var bag = await new InMemoryRepository<Bag>(context, time).SaveAsync(new Bag { BeanId = bean.Id, WeightG = 250m });
        var gear = await new InMemoryRepository<EquipmentItem>(context, time)
            .SaveAsync(new EquipmentItem { Kind = EquipmentKind.Grinder, Brand = "Comandante", GrindScale = Dialed.Domain.Equipment.GrindScale.Clicks(40) });

        bean.Visibility.ShouldBe(Visibility.Household);
        bag.Status.ShouldBe(Dialed.Domain.Inventory.BagStatus.Sealed);
        gear.GrindScale!.Max.ShouldBe(40m);
    }
}
