using FluentAssertions;
using PSR.Service.Api.Data.Entities;
using PSR.Service.Api.Users;
using Xunit;

namespace PSR.Service.Tests;

/// <summary>The rules the user-management pages are gated on. These are the server's answer, not the
/// desktop app's — the app only hides buttons.</summary>
public class UserHierarchyTests
{
    private const int Admin = UserHierarchy.RankAdmin;
    private const int Manager = UserHierarchy.RankManager;
    private const int Supervisor = UserHierarchy.RankSupervisor;
    private const int Other = UserHierarchy.RankOther;

    [Theory]
    [InlineData(RoleNames.Admin, Admin)]
    [InlineData(RoleNames.Manager, Manager)]
    [InlineData(RoleNames.Supervisor, Supervisor)]
    [InlineData(RoleNames.Technician, Other)]
    [InlineData(RoleNames.Accounts, Other)]
    public void Each_role_maps_to_its_rank(string role, int expected)
        => UserHierarchy.RankOf([role]).Should().Be(expected);

    [Fact]
    public void The_highest_role_on_an_account_decides_its_rank()
    {
        UserHierarchy.RankOf([RoleNames.Technician, RoleNames.Manager]).Should().Be(Manager);
        UserHierarchy.RankOf([RoleNames.Supervisor, RoleNames.Admin]).Should().Be(Admin);
        UserHierarchy.RankOf([]).Should().Be(Other);
    }

    // ---- the rules the user asked for, stated directly ----

    [Fact]
    public void A_manager_cannot_disable_an_admin()
        => UserHierarchy.CanManage(Manager, Admin).Should().BeFalse();

    [Fact]
    public void A_supervisor_can_neither_see_nor_touch_managers_or_other_supervisors()
    {
        UserHierarchy.CanView(Supervisor, Manager).Should().BeFalse();
        UserHierarchy.CanView(Supervisor, Supervisor).Should().BeFalse();
        UserHierarchy.CanManage(Supervisor, Manager).Should().BeFalse();
        UserHierarchy.CanManage(Supervisor, Supervisor).Should().BeFalse();
    }

    [Fact]
    public void An_admin_manages_managers_supervisors_and_everyone_else()
    {
        UserHierarchy.CanManage(Admin, Manager).Should().BeTrue();
        UserHierarchy.CanManage(Admin, Supervisor).Should().BeTrue();
        UserHierarchy.CanManage(Admin, Other).Should().BeTrue();
        // Other admins too: the last-active-admin and not-yourself guards cover the damaging cases,
        // and locking admins out of each other would strand an estate on a forgotten password.
        UserHierarchy.CanManage(Admin, Admin).Should().BeTrue();
    }

    [Fact]
    public void A_manager_manages_supervisors_and_below_but_not_other_managers()
    {
        UserHierarchy.CanManage(Manager, Supervisor).Should().BeTrue();
        UserHierarchy.CanManage(Manager, Other).Should().BeTrue();
        UserHierarchy.CanManage(Manager, Manager).Should().BeFalse();
    }

    [Fact]
    public void A_supervisor_manages_only_the_ranks_below()
    {
        UserHierarchy.CanManage(Supervisor, Other).Should().BeTrue();
        UserHierarchy.CanView(Supervisor, Other).Should().BeTrue();
    }

    [Fact]
    public void Nobody_below_supervisor_manages_anyone()
    {
        UserHierarchy.CanManage(Other, Other).Should().BeFalse();
        UserHierarchy.CanView(Other, Other).Should().BeFalse();
    }

    [Fact]
    public void A_manager_sees_admins_even_though_it_cannot_change_them()
        => UserHierarchy.CanView(Manager, Admin).Should().BeTrue();

    // ---- privilege escalation ----

    [Fact]
    public void Nobody_below_admin_can_grant_admin()
    {
        UserHierarchy.CanGrant(Manager, RoleNames.Admin).Should().BeFalse();
        UserHierarchy.CanGrant(Supervisor, RoleNames.Admin).Should().BeFalse();
        UserHierarchy.CanGrant(Admin, RoleNames.Admin).Should().BeTrue();
    }

    [Fact]
    public void Granting_is_capped_at_your_own_rank_not_just_below_the_target()
    {
        // A manager outranks a supervisor, but promoting one to manager would make a peer.
        UserHierarchy.CanGrant(Manager, RoleNames.Manager).Should().BeFalse();
        UserHierarchy.CanGrant(Manager, RoleNames.Supervisor).Should().BeTrue();
        UserHierarchy.CanGrant(Supervisor, RoleNames.Supervisor).Should().BeFalse();
        UserHierarchy.CanGrant(Supervisor, RoleNames.Technician).Should().BeTrue();
    }
}
