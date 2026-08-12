using FluentAssertions;
using PSR.Service.Api.MachineTests;
using Xunit;

namespace PSR.Service.Tests;

public class MachineRecordMapperTests
{
    private static MachineRawRow Row(params (string Column, string Value)[] cells) =>
        new(cells.Select(c => new KeyValuePair<string, string>(c.Column, c.Value)).ToList());

    [Theory]
    [InlineData("m_mb_no", "Mainboard")]
    [InlineData("InvDate", "Purchase date")]
    [InlineData("some_new_field", "Some new field")]   // a column added later still reads sensibly
    [InlineData("widget_no", "Widget")]                // the legacy "_no" suffix is noise once labelled
    public void Columns_get_readable_labels(string column, string expected)
        => MachineRecordMapper.Label(column).Should().Be(expected);

    [Fact]
    public void Machine_serial_wins_when_the_term_hits_several_columns()
    {
        // Same digits on the unit and on its pump: reporting the pump would misdescribe the record.
        var row = Row(("m_ser_no", "SN900"), ("m_pump_no", "SN900"));
        MachineRecordMapper.Map(row, "SN900").MatchedLabel.Should().Be("Machine serial");
    }

    [Fact]
    public void A_component_hit_is_reported_as_that_component()
    {
        var row = Row(("m_ser_no", "SN900"), ("m_mb_no", "MB123"));
        var record = MachineRecordMapper.Map(row, "mb12");

        record.MatchedField.Should().Be("m_mb_no");
        record.MatchedLabel.Should().Be("Mainboard");
        record.MatchedValue.Should().Be("MB123");
    }

    [Fact]
    public void Every_column_is_returned_in_column_order_with_blanks_as_null()
    {
        var row = Row(("m_ser_no", "SN900"), ("m_mb_no", "  "), ("Customer", "Sri Ram"));
        var fields = MachineRecordMapper.Map(row).Fields;

        fields.Select(f => f.Column).Should().Equal("m_ser_no", "m_mb_no", "Customer");
        fields[1].Value.Should().BeNull();
        fields[2].Value.Should().Be("Sri Ram");
    }

    [Fact]
    public void Warranty_is_computed_from_the_rows_own_figures()
    {
        var purchased = DateTime.UtcNow.Date.AddMonths(-6);
        var row = Row(("InvDate", purchased.ToString("yyyy-MM-dd")), ("Warranty", "24"));
        var record = MachineRecordMapper.Map(row);

        record.WarrantyMonths.Should().Be(24);
        record.WarrantyStatus.Should().Be("IN");
        record.WarrantyExpiry.Should().Be(purchased.AddMonths(24));
    }

    [Fact]
    public void An_expired_term_reads_out()
    {
        var row = Row(("InvDate", DateTime.UtcNow.Date.AddMonths(-30).ToString("yyyy-MM-dd")), ("Warranty", "24"));
        MachineRecordMapper.Map(row).WarrantyStatus.Should().Be("OUT");
    }

    [Fact]
    public void Missing_figures_leave_the_verdict_unknown()
    {
        MachineRecordMapper.Map(Row(("Warranty", "24"))).WarrantyStatus.Should().Be("UNKNOWN");
        MachineRecordMapper.Map(Row(("InvDate", "2024-01-15"))).WarrantyStatus.Should().Be("UNKNOWN");
    }
}
