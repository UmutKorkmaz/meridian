"""ZIP-level controls for NuGet's signature-independent baseline content hash."""

import base64
import hashlib
import io
from pathlib import Path
import struct
import sys
import tempfile
import unittest
from unittest import mock
import warnings
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import candidate_gate as gate  # noqa: E402


class NonSeekableZipBuffer(io.BytesIO):
    def seekable(self):
        return False

    def seek(self, *args):
        raise io.UnsupportedOperation('streaming ZIP fixture')


class NuGetContentHashTests(unittest.TestCase):
    def archive(self, signature_at=None, *, streaming=False, comment=b''):
        buffer = NonSeekableZipBuffer() if streaming else io.BytesIO()
        entries = [('first.txt', b'first payload'), ('nested/second.txt', b'second payload' * 10)]
        if signature_at is not None:
            entries.insert(signature_at, ('.signature.p7s', b'synthetic signature; no trust claim'))
        with zipfile.ZipFile(buffer, 'w') as archive:
            for name, content in entries:
                info = zipfile.ZipInfo(name, (2020, 1, 2, 3, 4, 6))
                info.compress_type = zipfile.ZIP_DEFLATED
                info.comment = b'entry comment'
                archive.writestr(info, content)
            archive.comment = comment
        return buffer.getvalue()

    def digest(self, data):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'baseline.nupkg'
            path.write_bytes(data)
            return gate.nuget_content_sha512(path)

    def test_unsigned_archive_keeps_its_complete_byte_hash(self):
        data = self.archive(comment=b'archive comment')
        expected = 'sha512-' + base64.b64encode(hashlib.sha512(data).digest()).decode('ascii')
        self.assertEqual(expected, self.digest(data))

    def test_signature_position_compression_comments_and_descriptors_preserve_unsigned_hash(self):
        for streaming in (False, True):
            for position in range(3):
                with self.subTest(streaming=streaming, position=position):
                    unsigned = self.archive(streaming=streaming, comment=b'archive comment')
                    signed = self.archive(position, streaming=streaming, comment=b'archive comment')
                    self.assertNotEqual(hashlib.sha512(unsigned).digest(), hashlib.sha512(signed).digest())
                    self.assertEqual(self.digest(unsigned), self.digest(signed))

    def test_duplicate_payload_names_are_rejected(self):
        buffer = io.BytesIO(self.archive(2))
        with warnings.catch_warnings(), zipfile.ZipFile(buffer, 'a') as archive:
            warnings.simplefilter('ignore', UserWarning)
            archive.writestr('first.txt', b'duplicate payload')
        with self.assertRaises(ValueError):
            self.digest(buffer.getvalue())

    def test_duplicate_signatures_are_rejected(self):
        buffer = io.BytesIO(self.archive(2))
        with warnings.catch_warnings(), zipfile.ZipFile(buffer, 'a') as archive:
            warnings.simplefilter('ignore', UserWarning)
            archive.writestr('.signature.p7s', b'another signature')
        with self.assertRaises(ValueError):
            self.digest(buffer.getvalue())

    def test_disagreeing_local_header_is_rejected(self):
        data = bytearray(self.archive(2))
        struct.pack_into('<H', data, 8, zipfile.ZIP_STORED)
        with self.assertRaises(ValueError):
            self.digest(data)

    def test_encrypted_entries_are_rejected(self):
        data = bytearray(self.archive(2))
        directory = data.index(b'PK\x01\x02')
        struct.pack_into('<H', data, directory + 8, 1)
        with self.assertRaises(ValueError):
            self.digest(data)

    def test_invalid_archive_is_rejected(self):
        with self.assertRaises(zipfile.BadZipFile):
            self.digest(b'not a package archive')

    def test_unsupported_signed_layouts_are_rejected(self):
        original = self.archive(2)
        first = original.index(b'PK\x01\x02')
        second = original.index(b'PK\x01\x02', first + 4)
        signature = original.index(b'PK\x01\x02', second + 4)
        variants = {}
        for label, offset, value, kind in (
                ('duplicate offset', second + 42, 0, '<I'),
                ('nonzero first offset', first + 42, 1, '<I'),
                ('ZIP64 size', first + 20, 0xffffffff, '<I'),
                ('signature UTF8 flag', signature + 8, 1 << 11, '<H')):
            data = bytearray(original)
            struct.pack_into(kind, data, offset, value)
            variants[label] = data
        variants['trailing data'] = original + b'unsupported trailer'
        for label, data in variants.items():
            with self.subTest(layout=label), self.assertRaises((ValueError, struct.error, zipfile.BadZipFile)):
                self.digest(data)
        with self.subTest(layout='size cap'), tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'oversize.nupkg'
            path.write_bytes(original)
            with mock.patch.object(Path, 'stat', return_value=mock.Mock(st_size=64 * 1024 * 1024 + 1)):
                with self.assertRaisesRegex(ValueError, 'unsupported signed baseline size'):
                    gate.nuget_content_sha512(path)


if __name__ == '__main__':
    unittest.main()
