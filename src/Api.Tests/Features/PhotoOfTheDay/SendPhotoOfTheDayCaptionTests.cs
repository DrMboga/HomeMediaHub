using Api.Features.PhotoOfTheDay;

namespace Api.Tests.Features.PhotoOfTheDay;

public sealed class SendPhotoOfTheDayCaptionTests
{
    private static DateTime At(int y, int mo, int d, int h = 12, int mi = 0) =>
        new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    [Fact]
    public void BuildCaption_CombinesDateTimeAndAddress()
    {
        Assert.Equal(
            "17.08.2019 14:32 · Berlin, Deutschland",
            SendPhotoOfTheDayHandler.BuildCaption(At(2019, 8, 17, 14, 32), "Berlin, Deutschland"));
    }

    [Fact]
    public void BuildCaption_PadsDayMonthAndHour()
    {
        Assert.Equal("02.05.2011 09:05", SendPhotoOfTheDayHandler.BuildCaption(At(2011, 5, 2, 9, 5), null));
    }

    [Fact]
    public void BuildCaption_DateOnlyWhenThePhotoHasNoAddress()
    {
        // 63% of the library is geotagged, so this is the common case for the other 37%.
        Assert.Equal("02.05.2011 12:00", SendPhotoOfTheDayHandler.BuildCaption(At(2011, 5, 2), null));
        Assert.Equal("02.05.2011 12:00", SendPhotoOfTheDayHandler.BuildCaption(At(2011, 5, 2), "   "));
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
    public void BuildCaption_DoesNotShiftTheStoredWallClockTime()
    {
        // taken_at is the camera's naive wall-clock time, tagged UTC without being shifted. A
        // photo taken at 17:00 in Crete must read 17:00 — converting to the host's local time or
        // to Potd:TimeZone is the +2h error found on stage.
        var atFive = new DateTime(2019, 8, 17, 17, 0, 0, DateTimeKind.Utc);

        Assert.StartsWith("17.08.2019 17:00", SendPhotoOfTheDayHandler.BuildCaption(atFive, "Chania, Ελλάδα"));
    }

    [Fact]
    public void BuildCaption_IsCultureIndependent()
    {
        // The container runs under the invariant culture unless told otherwise; a locale that
        // uses a different date separator or a 12-hour clock must not change the caption.
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("en-US");
            Assert.Equal("17.08.2019 14:32", SendPhotoOfTheDayHandler.BuildCaption(At(2019, 8, 17, 14, 32), null));

            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("17.08.2019 14:32", SendPhotoOfTheDayHandler.BuildCaption(At(2019, 8, 17, 14, 32), null));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }
}
