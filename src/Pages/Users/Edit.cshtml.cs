using Matddns.Models;
using Matddns.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matddns.Pages.Users;

public class EditModel : PageModel
{
    private readonly ConfigService _config;
    private readonly AuthService _auth;
    public EditModel(ConfigService config, AuthService auth)
    {
        _config = config;
        _auth = auth;
    }

    public string? Username { get; private set; } // null = new
    public bool IsNew => Username == null;
    public bool IsCurrentUser => string.Equals(Username, User.Identity?.Name, StringComparison.OrdinalIgnoreCase);

    [TempData] public string? Notice { get; set; }
    [TempData] public string? Error { get; set; }

    public IActionResult OnGet(string? name)
    {
        if (!string.IsNullOrEmpty(name))
        {
            var u = _config.Read(c => c.Users.FirstOrDefault(x => x.Username.Equals(name, StringComparison.OrdinalIgnoreCase)));
            if (u == null) { Error = "User not found"; return RedirectToPage("/System"); }
            Username = u.Username;
        }
        return Page();
    }

    public IActionResult OnPostCreate(string NewUsername, string NewPassword)
    {
        var nm = (NewUsername ?? "").Trim();
        if (string.IsNullOrWhiteSpace(nm) || string.IsNullOrEmpty(NewPassword))
        {
            Error = "Username and password required";
            return RedirectToPage("Edit");
        }
        var exists = _config.Read(c => c.Users.Any(u => u.Username.Equals(nm, StringComparison.OrdinalIgnoreCase)));
        if (exists) { Error = $"User '{nm}' already exists"; return RedirectToPage("Edit"); }

        _config.Mutate(c => c.Users.Add(new UserAccount { Username = nm, PasswordHash = _auth.Hash(NewPassword) }));
        Notice = $"User '{nm}' created";
        return RedirectToPage("/System");
    }

    public IActionResult OnPostSave(string Name, string? NewPassword)
    {
        if (!string.IsNullOrEmpty(NewPassword))
        {
            _config.Mutate(c =>
            {
                var u = c.Users.FirstOrDefault(x => x.Username.Equals(Name, StringComparison.OrdinalIgnoreCase));
                if (u != null) u.PasswordHash = _auth.Hash(NewPassword);
            });
            Notice = $"Password for '{Name}' updated";
        }
        else
        {
            Notice = "Nothing changed";
        }
        return RedirectToPage("Edit", new { name = Name });
    }

    public IActionResult OnPostDelete(string Name)
    {
        // cannot delete the account you are logged in as (lock-out protection)
        if (string.Equals(Name, User.Identity?.Name, StringComparison.OrdinalIgnoreCase))
        {
            Error = "You cannot delete the user you are logged in as.";
            return RedirectToPage("Edit", new { name = Name });
        }
        _config.Mutate(c =>
        {
            if (c.Users.Count > 1)
                c.Users.RemoveAll(u => u.Username.Equals(Name, StringComparison.OrdinalIgnoreCase));
        });
        Notice = $"User '{Name}' deleted";
        return RedirectToPage("/System");
    }
}
