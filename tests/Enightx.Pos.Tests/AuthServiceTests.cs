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
}
