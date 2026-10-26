using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace WebhookInbox.Tests;

public class DatabaseErrorTests
{
    private static DbUpdateException Failure(string sqlState) =>
        new("save failed", new PostgresException("text", "ERROR", "ERROR", sqlState));

    [Theory]
    [InlineData("22021")]   // a character the encoding cannot hold (a NUL)
    [InlineData("22P02")]   // invalid text representation
    [InlineData("22001")]   // string too long for the column
    [InlineData("54000")]   // program limit exceeded: an index entry that is too big
    public void IsUnstorableData_ErrorsAboutTheDataItself_AreRecognized(string sqlState)
    {
        Assert.True(Failure(sqlState).IsUnstorableData());
    }

    [Theory]
    [InlineData("23505")]   // unique violation: a duplicate, handled apart
    [InlineData("23503")]   // foreign key violation
    [InlineData("25P02")]   // transaction aborted
    [InlineData("28P01")]   // wrong password
    [InlineData("40001")]   // serialization failure
    [InlineData("57014")]   // query canceled: says nothing about the data
    [InlineData("53300")]   // too many connections
    public void IsUnstorableData_ErrorsThatAreNotAboutTheData_AreNot(string sqlState)
    {
        Assert.False(Failure(sqlState).IsUnstorableData());
    }

    [Fact]
    public void IsUnstorableData_AnErrorThatIsNotFromTheDatabase_IsNot()
    {
        Assert.False(new DbUpdateException("save failed", new TimeoutException()).IsUnstorableData());
    }

    [Fact]
    public void IsUniqueViolation_OnlyTheUniqueViolation_IsRecognized()
    {
        Assert.True(Failure("23505").IsUniqueViolation());
        Assert.False(Failure("23503").IsUniqueViolation());
    }
}
