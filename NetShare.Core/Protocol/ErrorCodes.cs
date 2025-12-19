namespace NetShare.Core.Protocol
{
    public static class ErrorCodes
    {
        public const string BadRequest = "BAD_REQUEST";
        public const string UnsupportedVersion = "UNSUPPORTED_VERSION";
        public const string AuthRequired = "AUTH_REQUIRED";
        public const string AuthFailed = "AUTH_FAILED";
        public const string NotFound = "NOT_FOUND";
        public const string ReadOnly = "READ_ONLY";
        public const string PathTraversal = "PATH_TRAVERSAL";
        public const string IoError = "IO_ERROR";
        public const string IntegrityFailed = "INTEGRITY_FAILED";
        public const string InternalError = "INTERNAL_ERROR";
        public const string InvalidRange = "INVALID_RANGE";
    }
}
