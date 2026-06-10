using Matddns.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matddns.Pages;

public class IndexModel : PageModel
{
    private readonly StatusService _status;
    public IndexModel(StatusService status) => _status = status;

    public StatusSnapshot Snapshot { get; private set; } = new();

    public void OnGet() => Snapshot = _status.Build(15);
}
