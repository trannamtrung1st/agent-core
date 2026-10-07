"""Keep hosted Ubuntu browser dependency installation bounded and reliable."""

import argparse
from pathlib import Path


def prepare(apt_dir: Path) -> None:
    # Hosted images can use either direct sources or file-based mirror lists.
    # The Azure mirror stalled twice; retain Ubuntu packages via its HTTPS archive.
    sources = [apt_dir / "sources.list", *apt_dir.glob("apt-mirrors*.txt")]
    source_dir = apt_dir / "sources.list.d"
    sources.extend(source_dir.glob("*.list"))
    sources.extend(source_dir.glob("*.sources"))
    for source in sources:
        if not source.is_file():
            continue
        original = source.read_text()
        updated = original.replace("http://azure.archive.ubuntu.com/ubuntu", "https://archive.ubuntu.com/ubuntu")
        updated = updated.replace("https://azure.archive.ubuntu.com/ubuntu", "https://archive.ubuntu.com/ubuntu")
        if updated != original:
            source.write_text(updated)
    config_dir = apt_dir / "apt.conf.d"
    config_dir.mkdir(exist_ok=True)
    (config_dir / "80-ci-network").write_text(
        'Acquire::http::Timeout "30";\n'
        'Acquire::https::Timeout "30";\n'
        'Acquire::Retries "2";\n'
    )


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apt-dir", type=Path, default=Path("/etc/apt"))
    prepare(parser.parse_args().apt_dir)
