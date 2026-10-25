from __future__ import annotations


SERVICE_NAME = "shop-auth-codes"


class CredentialError(RuntimeError):
    """Raised when the Windows credential store cannot provide a secret."""


class CredentialStore:
    def __init__(self, backend=None, service: str = SERVICE_NAME):
        if backend is None:
            try:
                import keyring
            except ImportError as exc:
                raise CredentialError("Install keyring before using mailbox credentials") from exc
            backend = keyring
        self.backend = backend
        self.service = service

    def get(self, username: str) -> str:
        value = self.backend.get_password(self.service, username)
        if not value:
            raise CredentialError(
                f"No mailbox client token found for {username}; run setup first"
            )
        return value

    def set(self, username: str, secret: str) -> None:
        if not secret:
            raise CredentialError("Mailbox client token cannot be empty")
        self.backend.set_password(self.service, username, secret)
