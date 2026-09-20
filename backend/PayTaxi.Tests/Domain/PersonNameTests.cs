using PayTaxi.Core.Identity;
using Xunit;

namespace PayTaxi.Tests.Domain;

public class PersonNameTests
{
    [Theory]
    [InlineData("გიორგი მამულაშვილი", "Giorgi Mamulashvili")]     // Georgian vs Latin
    [InlineData("გიორგი მამულაშვილი", "Гиорги Мамулашвили")]       // Georgian vs Russian
    [InlineData("Giorgi Mamulashvili", "MAMULASHVILI GIORGI")]     // word order + case
    [InlineData("Giorgi Mamulashvili", "Giorgi Tornike Mamulashvili")] // extra middle name
    [InlineData("Levan Kobakhidze", "levan  kobakhidze ")]          // whitespace
    [InlineData("თამარ ბერიძე", "Tamar Beridze")]                    // თ → t
    [InlineData("ნინო ჩხეიძე", "Nino Chkheidze")]                     // ჩ → ch, ხ → kh
    // Common Latin spellings that diverge from the strict table:
    [InlineData("გიორგი ღვინიაშვილი", "Giorgi Gviniashvili")]         // ღ written g, not gh
    [InlineData("ლევან ყიფიანი", "Levan Kipiani")]                     // ყ written k, not q
    [InlineData("ნიკა ჯავახიშვილი", "Nika Javakhishvili")]             // ჯ j
    [InlineData("ზურაბ ჟვანია", "Zurab Jvania")]                       // ჟ written j, not zh
    [InlineData("დავით წერეთელი", "Davit Tsereteli")]                  // წ ts
    [InlineData("ილია ჭავჭავაძე", "Ilia Chavchavadze")]                // ჭ ch, ძ dz
    [InlineData("გიგა ფირცხალავა", "Giga Firtskhalava")]               // ფ written f
    [InlineData("ნიკოლოზ მიქელაძე", "Nikoloz Mikelladze")]             // doubled letter
    [InlineData("სოფო ხაჩიძე", "Sopho Hachidze")]                      // ფ ph, ხ written h
    [InlineData("ლევან კობახიძე", "Levan Kobahidze")]                  // kh written h — a variant, not a typo
    public void Same_person_across_scripts_and_order(string a, string b)
    {
        Assert.True(PersonName.LooksLikeSamePerson(a, b));
        Assert.True(PersonName.LooksLikeSamePerson(b, a));
    }

    [Theory]
    [InlineData("გიორგი მამულაშვილი", "ნინო ბერიძე")]                 // different person, same script
    [InlineData("Giorgi Mamulashvili", "Nino Beridze")]              // different person, Latin
    [InlineData("გიორგი მამულაშვილი", "Nino Beridze")]               // different person, cross-script
    [InlineData("Giorgi Mamulashvili", "Giorgi Beridze")]            // shares first name only, both 2 words → surname differs
    [InlineData("ლევან კობახიძე", "Levan Kobakhadze")]                 // genuine typo (i→a) still differs
    [InlineData("Георгий Кобахидзе", "Giorgi Kobakhidze")]             // Russian given-name form is a different word (known limitation)
    public void Different_person_is_rejected(string a, string b)
    {
        Assert.False(PersonName.LooksLikeSamePerson(a, b));
    }

    [Fact]
    public void Empty_or_unusable_names_never_match()
    {
        Assert.False(PersonName.LooksLikeSamePerson("", "Giorgi Mamulashvili"));
        Assert.False(PersonName.LooksLikeSamePerson("Giorgi Mamulashvili", null));
        Assert.False(PersonName.LooksLikeSamePerson("123 ---", "Giorgi Mamulashvili"));
    }

    [Fact]
    public void Transliteration_yields_lowercase_latin_only()
    {
        Assert.Equal("mamulasvili", PersonName.Transliterate("მამულაშვილი")); // sh folded to s
        Assert.Equal("giorgi", PersonName.Transliterate("Гиорги"));
        Assert.Equal("jose", PersonName.Transliterate("José"));
        Assert.Equal(PersonName.Transliterate("ღვინიაშვილი"), PersonName.Transliterate("Gviniashvili"));
    }
}
