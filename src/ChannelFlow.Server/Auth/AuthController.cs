using System.Security.Claims;
using FinTv.Data;
using FinTv.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FinTv.Auth;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly FinTvDbContext _db;

    public AuthController(FinTvDbContext db)
    {
        _db = db;
    }

    [HttpGet("status")]
    [AllowAnonymous]
    public async Task<ActionResult<object>> Status(CancellationToken cancellationToken)
    {
        var hasUser = await _db.AdminUsers.AnyAsync(cancellationToken);
        return Ok(new
        {
            needsSetup = !hasUser,
            authenticated = User.Identity?.IsAuthenticated == true,
            userName = User.Identity?.Name
        });
    }

    [HttpPost("setup")]
    [AllowAnonymous]
    public async Task<IActionResult> Setup([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        if (await _db.AdminUsers.AnyAsync(cancellationToken))
        {
            return Conflict(new { message = "Admin user already exists." });
        }

        if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { message = "Username and password are required." });
        }

        if (request.Password.Length < 8)
        {
            return BadRequest(new { message = "Password must be at least 8 characters." });
        }

        var userName = request.UserName.Trim();
        _db.AdminUsers.Add(new AdminUser
        {
            UserName = userName,
            PasswordHash = Services.PasswordHasher.Hash(request.Password)
        });
        await _db.SaveChangesAsync(cancellationToken);
        await SignInAsync(userName, request.RememberMe);
        return Ok(new { authenticated = true, userName });
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        var user = await _db.AdminUsers.FirstOrDefaultAsync(
            u => u.UserName == request.UserName,
            cancellationToken);
        if (user is null || !Services.PasswordHasher.Verify(request.Password ?? string.Empty, user.PasswordHash))
        {
            return Unauthorized(new { message = "Invalid username or password." });
        }

        await SignInAsync(user.UserName, request.RememberMe);
        return Ok(new { authenticated = true, userName = user.UserName });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Ok();
    }

    [HttpPost("password")]
    [Authorize(Policy = "admin")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        var user = await _db.AdminUsers.FirstOrDefaultAsync(u => u.UserName == User.Identity!.Name, cancellationToken);
        if (user is null || !Services.PasswordHasher.Verify(request.CurrentPassword ?? string.Empty, user.PasswordHash))
        {
            return BadRequest(new { message = "Current password is incorrect." });
        }

        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 8)
        {
            return BadRequest(new { message = "New password must be at least 8 characters." });
        }

        user.PasswordHash = Services.PasswordHasher.Hash(request.NewPassword);
        await _db.SaveChangesAsync(cancellationToken);
        return Ok();
    }

    private Task SignInAsync(string userName, bool rememberMe)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, userName),
            new(ClaimTypes.Role, "admin")
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var props = new AuthenticationProperties
        {
            IsPersistent = rememberMe
        };
        if (rememberMe)
        {
            props.ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30);
        }

        return HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            props);
    }
}

public class LoginRequest
{
    public string? UserName { get; set; }

    public string? Password { get; set; }

    public bool RememberMe { get; set; }
}

public class ChangePasswordRequest
{
    public string? CurrentPassword { get; set; }

    public string? NewPassword { get; set; }
}

public sealed class ApiKeyMiddleware
{
    private readonly RequestDelegate _next;

    public ApiKeyMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        // Programme posters are <img> URLs in XMLTV; the browser cannot send the IPTV API key.
        var isIptvPoster = path.StartsWith("/iptv/poster/", StringComparison.OrdinalIgnoreCase);
        var needsApiKey = !isIptvPoster
            && (path.StartsWith("/iptv", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/api/plugin", StringComparison.OrdinalIgnoreCase));

        if (needsApiKey)
        {
            var provided = context.Request.Headers["X-Api-Key"].FirstOrDefault()
                ?? context.Request.Query["apiKey"].FirstOrDefault();
            if (!PluginApiKey.Matches(provided))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { message = "Invalid API key." });
                return;
            }
        }

        await _next(context);
    }
}
