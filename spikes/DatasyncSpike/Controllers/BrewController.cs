using CommunityToolkit.Datasync.Server;
using DatasyncSpike.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DatasyncSpike.Controllers;

[Authorize]
[Route("tables/brews")]
public sealed class BrewController : TableController<Brew>
{
    public BrewController(IRepository<Brew> repository, IAccessControlProvider<Brew> accessControl)
        : base(repository, accessControl)
    {
        Options = new TableControllerOptions { EnableSoftDelete = true };
    }
}
