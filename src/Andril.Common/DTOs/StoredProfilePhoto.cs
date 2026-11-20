using Andril.Common.Accounts.Profiles;

namespace Andril.Common.DTOs;

// Evidence for backend components only. Controllers return PrivatePhotoStatus or JPEG bytes.
public sealed record StoredProfilePhoto(PhotoObjectAddress Address, PhotoState State, bool IsCurrent,
    string Sha256, string MediaType, int Length, int Width, int Height);
