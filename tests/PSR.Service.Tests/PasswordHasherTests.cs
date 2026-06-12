using FluentAssertions;
using PSR.Service.Api.Auth;
using Xunit;

namespace PSR.Service.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void Hash_then_Verify_succeeds()
    {
        var hash = PasswordHasher.Hash("CorrectHorseBatteryStaple");
        PasswordHasher.Verify("CorrectHorseBatteryStaple", hash).Should().BeTrue();
    }

    [Fact]
    public void Verify_fails_for_wrong_password()
    {
        var hash = PasswordHasher.Hash("right");
        PasswordHasher.Verify("wrong", hash).Should().BeFalse();
    }

    [Fact]
    public void Hash_produces_different_output_for_same_input()
    {
        var hash1 = PasswordHasher.Hash("same");
        var hash2 = PasswordHasher.Hash("same");
        hash1.Should().NotBe(hash2, "BCrypt uses a random salt per call");
        PasswordHasher.Verify("same", hash1).Should().BeTrue();
        PasswordHasher.Verify("same", hash2).Should().BeTrue();
    }
}
