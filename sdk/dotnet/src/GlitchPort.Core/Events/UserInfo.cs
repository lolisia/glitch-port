namespace GlitchPort
{
    /// <summary>Information about the user affected by an event.</summary>
    public sealed class UserInfo
    {
        /// <summary>Gets or sets the stable user identifier.</summary>
        public string? Id { get; set; }

        /// <summary>Gets or sets the user name.</summary>
        public string? Username { get; set; }

        /// <summary>Gets or sets the e-mail address.</summary>
        public string? Email { get; set; }

        /// <summary>
        /// Gets or sets the IPv4 or IPv6 address of the user. The literal
        /// <c>{{auto}}</c> asks the Relay to substitute the request address.
        /// </summary>
        public string? IpAddress { get; set; }
    }
}
