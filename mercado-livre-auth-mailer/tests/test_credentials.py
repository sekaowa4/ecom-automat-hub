import unittest

from shop_auth_codes.credentials import CredentialStore


class FakeKeyring:
    def __init__(self):
        self.values = {}

    def get_password(self, service, username):
        return self.values.get((service, username))

    def set_password(self, service, username, password):
        self.values[(service, username)] = password


class CredentialTests(unittest.TestCase):
    def test_stores_and_reads_secret_through_keyring_backend(self):
        backend = FakeKeyring()
        credentials = CredentialStore(backend, service="test-service")

        credentials.set("sender@example.com", "client-token")

        self.assertEqual(credentials.get("sender@example.com"), "client-token")


if __name__ == "__main__":
    unittest.main()
