using Api.Features.PhotoOfTheDay;

namespace Api.Tests.Features.PhotoOfTheDay;

public sealed class SendPhotoOfTheDayCaptionTests
{
    private static DateTime At(int y, int m, int d) => new(y, m, d, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void BuildCaption_CombinesDateAndAddress()
    {
        Assert.Equal(
            "17 August 2019 · Berlin, Deutschland",
            SendPhotoOfTheDayHandler.BuildCaption(At(2019, 8, 17), "Berlin, Deutschland"));
    }

    [Fact]
    public void BuildCaption_DateOnlyWhenThePhotoHasNoAddress()
    {
        // 63% of the library is geotagged, so this is the common case for the other 37%.
        Assert.Equal("2 May 2011", SendPhotoOfTheDayHandler.BuildCaption(At(2011, 5, 2), null));
        Assert.Equal("2 May 2011", SendPhotoOfTheDayHandler.BuildCaption(At(2011, 5, 2), "   "));
    }

    [Fact]
    public void BuildCaption_AddressOnlyWhenThePhotoIsUndated()
    {
        Assert.Equal("Paris, France", SendPhotoOfTheDayHandler.BuildCaption(null, "Paris, France"));
    }

    [Fact]
    public void BuildCaption_NeverEmpty()
    {
        // Telegram accepts an empty caption, but a bare photo with no context is worse than a
        // generic label — and an empty string here would read as a bug in the admin response.
        Assert.Equal("Photo of the Day", SendPhotoOfTheDayHandler.BuildCaption(null, null));
    }

    [Fact]
    public void BuildCaption_IsCultureIndependent()
    {
        // The container runs under the invariant culture unless told otherwise; the caption must
        // read the same either way rather than following the host's locale.
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("17 August 2019", SendPhotoOfTheDayHandler.BuildCaption(At(2019, 8, 17), null));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }
}
