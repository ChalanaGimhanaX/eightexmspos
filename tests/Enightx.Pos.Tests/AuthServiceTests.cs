using Xunit;
using Enightx.Pos.Common;
using Enightx.Pos.Domain;
using Enightx.Pos.Services;
using Enightx.Pos.Storage;

namespace Enightx.Pos.Tests;

public class AuthServiceTests : IDisposable
{
    private readonly PosDatabase _db;
    private readonly AuthService _auth;

    public AuthServiceTests()
    {
        _db = PosDatabase.CreateInMemory();
        _auth = new AuthService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task Authenticate_ValidCredentials_ReturnsUser()
    {
        var created = await _auth.CreateUserAsync("kasun", "Kasun Perera", "SecureP@ss1", Role.Cashier);
        Assert.NotNull(created);
        Assert.StartsWith("usr_", created.UserId);

        var loggedIn = await _auth.AuthenticateAsync("kasun", "SecureP@ss1", "T01", "B01", "C01");
        Assert.NotNull(loggedIn);
        Assert.Equal(created.UserId, loggedIn.UserId);
        Assert.Equal(Role.Cashier, loggedIn.Role);
    }

    [Fact]
    public async Task Authenticate_InvalidPassword_ThrowsAuthenticationException()
    {
        await _auth.CreateUserAsync("kamal", "Kamal Silva", "CorrectP@ss1", Role.Cashier);

        await Assert.ThrowsAsync<AuthenticationException>(() =>
            _auth.AuthenticateAsync("kamal", "WrongP@ss1", "T01", "B01", "C01")
        );
    }

    [Fact]
    public async Task Authenticate_NonExistentUser_ThrowsAuthenticationException()
    {
        await Assert.ThrowsAsync<AuthenticationException>(() =>
            _auth.AuthenticateAsync("nonexistent", "SomeP@ss1", "T01", "B01", "C01")
        );
    }

    [Fact]
    public async Task GetAllUsers_ReturnsAllCreatedUsers()
    {
        await _auth.CreateUserAsync("user_a", "Alice Bandara", "PassA123", Role.Cashier);
        await _auth.CreateUserAsync("user_b", "Bob Silva", "PassB123", Role.Manager);

        var all = await _auth.GetAllUsersAsync();
        Assert.True(all.Count >= 2);
        Assert.Contains(all, u => u.Username == "user_a");
        Assert.Contains(all, u => u.Username == "user_b");
    }

    [Fact]
    public async Task UpdateUserRole_UpdatesRoleAndPersists()
    {
        var user = await _auth.CreateUserAsync("cashier_promo", "Promo User", "Pass1234", Role.Cashier);
        Assert.Equal(Role.Cashier, user.Role);

        await _auth.UpdateUserRoleAsync(user.UserId, Role.Manager, "admin_user", "T01", "B01", "C01");

        var all = await _auth.GetAllUsersAsync();
        var updated = all.First(u => u.UserId == user.UserId);
        Assert.Equal(Role.Manager, updated.Role);

        var loggedIn = await _auth.AuthenticateAsync("cashier_promo", "Pass1234", "T01", "B01", "C01");
        Assert.Equal(Role.Manager, loggedIn.Role);
    }

    [Fact]
    public async Task SetUserActive_DeactivatesAndPreventsLogin()
    {
        var user = await _auth.CreateUserAsync("active_user", "Active User", "Pass1234", Role.Cashier);

        // Deactivate
        await _auth.SetUserActiveAsync(user.UserId, false, "admin_user", "T01", "B01", "C01");

        var all = await _auth.GetAllUsersAsync();
        var updated = all.First(u => u.UserId == user.UserId);
        Assert.False(updated.IsActive);

        // Login should now fail with AuthenticationException
        var ex = await Assert.ThrowsAsync<AuthenticationException>(() =>
            _auth.AuthenticateAsync("active_user", "Pass1234", "T01", "B01", "C01")
        );
        Assert.Equal("User account is inactive.", ex.Message);

        // Reactivate
        await _auth.SetUserActiveAsync(user.UserId, true, "admin_user", "T01", "B01", "C01");
        var loggedIn = await _auth.AuthenticateAsync("active_user", "Pass1234", "T01", "B01", "C01");
        Assert.True(loggedIn.IsActive);
    }

    [Fact]
    public async Task ResetPassword_AllowsLoginWithNewPassword()
    {
        var user = await _auth.CreateUserAsync("reset_user", "Reset User", "OldPass123", Role.Cashier);

        // Reset password
        await _auth.ResetPasswordAsync(user.UserId, "NewPass456!", "admin_user", "T01", "B01", "C01");

        // Old password must fail
        await Assert.ThrowsAsync<AuthenticationException>(() =>
            _auth.AuthenticateAsync("reset_user", "OldPass123", "T01", "B01", "C01")
        );

        // New password must succeed
        var loggedIn = await _auth.AuthenticateAsync("reset_user", "NewPass456!", "T01", "B01", "C01");
        Assert.NotNull(loggedIn);
        Assert.Equal(user.UserId, loggedIn.UserId);
    }
}
