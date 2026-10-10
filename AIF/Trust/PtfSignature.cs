using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace AIF.Trust;

// THE SIGNATURE OF A PTF OBJECT (MPAI-PTF V1.0, Data Conventions 2 and 3). As the
// Signature Conventions say: a signed object carries its Signature - the signature
// value, hexadecimal ASCII - and the KeyID of the key that verifies it. The
// signature is computed over the canonical form of the object without its Signature
// (the KeyID included), and verified the same way, so that a change to anything
// else in the object, at any depth, is detected, and a change of member order or
// whitespace is not. The algorithm is the key's: the identity of a key states it.
//
// Where a schema places the Signature and KeyID in a member rather than at the top -
// the Integrity of a Cryptographic Instance Identity - the holder names that member;
// the signature still covers the whole object.
//
// Choices where PTF does not say (M3223 3.7, agreed by the author):
//   - an ECDSA signature is r and s, 32 bytes each, concatenated (IEEE P1363);
//   - hexadecimal in upper case.
public static class PtfSignature
{
    public const string Member = "Signature";
    public const string KeyMember = "KeyID";

    public enum Outcome { Valid, Missing, UnknownKey, Malformed, Invalid }

    // Signs the object in place: any Signature it had is replaced; the KeyID is set.
    public static JsonObject Sign(JsonObject obj, ECDsa key, string keyId, string? holder = null)
    {
        var place = Holder(obj, holder, create: true)!;
        place.Remove(Member);
        place[KeyMember] = keyId;
        var value = key.SignData(PtfCanonical.Bytes(obj), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        place[Member] = Convert.ToHexString(value);
        return obj;
    }

    // keyFor: the public key a KeyID names, or null where the verifier knows none.
    public static Outcome Verify(JsonObject obj, Func<string, ECDsa?> keyFor, string? holder = null)
    {
        var place = Holder(obj, holder, create: false);
        if ((string?)place?[Member] is not { } hex) return Outcome.Missing;
        if ((string?)place[KeyMember] is not { } keyId || keyFor(keyId) is not { } key) return Outcome.UnknownKey;
        byte[] value;
        try { value = Convert.FromHexString(hex); }
        catch (FormatException) { return Outcome.Malformed; }
        if (value.Length != 64) return Outcome.Malformed;

        var unsigned = obj.DeepClone().AsObject();
        Holder(unsigned, holder, create: false)!.Remove(Member);
        return key.VerifyData(PtfCanonical.Bytes(unsigned), value, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
            ? Outcome.Valid
            : Outcome.Invalid;
    }

    private static JsonObject? Holder(JsonObject obj, string? holder, bool create)
    {
        if (holder is null) return obj;
        if (obj[holder] is JsonObject inner) return inner;
        if (!create) return null;
        var made = new JsonObject();
        obj[holder] = made;
        return made;
    }
}
