#!/bin/bash
# HomeMediaHub — fresh Ubuntu Server 24.04 bootstrap
# Run as a non-root user with sudo rights via:
#   bash server-setup.sh
set -e

echo "==> [1/6] System update"
sudo apt-get update -y
sudo apt-get upgrade -y

echo "==> [2/6] Install Docker (official repo)"
sudo apt-get install -y ca-certificates curl
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg \
    -o /etc/apt/keyrings/docker.asc
sudo chmod a+r /etc/apt/keyrings/docker.asc

echo \
  "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] \
  https://download.docker.com/linux/ubuntu \
  $(. /etc/os-release && echo "$VERSION_CODENAME") stable" | \
  sudo tee /etc/apt/sources.list.d/docker.list > /dev/null

sudo apt-get update -y
sudo apt-get install -y \
    docker-ce \
    docker-ce-cli \
    containerd.io \
    docker-buildx-plugin \
    docker-compose-plugin

# Allow current user to run docker without sudo
sudo usermod -aG docker "$USER"
echo "    Docker installed. NOTE: log out and back in for group membership to take effect."

echo "==> [3/6] Install Samba"
sudo apt-get install -y samba

echo "==> [4/6] Create host directories"

# App directory
# sudo mkdir -p /opt/homemediahub/app

# ── Stage (SSD — no USB HDD required) ──────────────────────────────────────
sudo mkdir -p /opt/homemediahub/stage/TestPhoto/iPhone
sudo mkdir -p /opt/homemediahub/stage/thumbnails
sudo mkdir -p /opt/homemediahub/stage/video-cache
sudo mkdir -p /opt/homemediahub/stage/display-cache
sudo mkdir -p /opt/homemediahub/stage/data/postgres
sudo mkdir -p /opt/homemediahub/stage/data/immich-postgres
sudo mkdir -p /opt/homemediahub/stage/data/geoservice-postgres
sudo mkdir -p /opt/homemediahub/stage/data/n8n
sudo mkdir -p /tmp/homemediahub-stage

# ── Prod (SATA HDD — /mnt/storage must be mounted first) ──────────────────
sudo mkdir -p /opt/homemediahub/data/postgres
sudo mkdir -p /opt/homemediahub/data/immich-postgres
sudo mkdir -p /opt/homemediahub/data/geoservice-postgres
sudo mkdir -p /opt/homemediahub/data/n8n
# TODO: 1 Make this when HDD mounted
# sudo mkdir -p /mnt/storage/Photo/iPhone
# sudo mkdir -p /mnt/storage/thumbnails
# sudo mkdir -p /mnt/storage/video-cache
# sudo mkdir -p /mnt/storage/display-cache
sudo mkdir -p /tmp/homemediahub-prod

# Fix ownership so docker containers (running as non-root) can write
sudo chown -R "$USER":"$USER" /opt/homemediahub
# TODO: 2 Make this when HDD mounted
# sudo chown -R "$USER":"$USER" /mnt/storage
sudo chown -R "$USER":"$USER" /tmp/homemediahub-stage
sudo chown -R "$USER":"$USER" /tmp/homemediahub-prod

# TODO: 3 Make this when HDD mounted
# echo "==> [5/6] Configure Samba share"
# # Create mediahub group and add current user
# sudo groupadd -f mediahub
# sudo usermod -aG mediahub "$USER"

# # Back up original smb.conf then append our share
# sudo cp /etc/samba/smb.conf /etc/samba/smb.conf.bak

# sudo tee -a /etc/samba/smb.conf > /dev/null <<'SAMBA'

# [Storage]
#    path = /mnt/storage
#    browseable = yes
#    read only = no
#    guest ok = no
#    valid users = @mediahub
#    create mask = 0664
#    directory mask = 0775
# SAMBA

# # Set a Samba password for the current user
# echo ""
# echo "    Set a Samba password for user '$USER' (used from Windows/macOS):"
# sudo smbpasswd -a "$USER"

# sudo systemctl enable smbd
# sudo systemctl restart smbd

echo "==> [6/6] Make postgres init script executable"
chmod +x /opt/homemediahub/app/infra/postgres/init/01-init.sh

echo ""
echo "================================================================"
echo " Setup complete. Next steps:"
echo ""
echo " 1. Log out and back in (required for docker group membership)."
echo ""
echo " 2. Copy your .env.example to .env and fill in values:"
echo "      cd /opt/homemediahub/app"
echo "      cp .env.example .env && nano .env"
echo ""
echo " 3. Build images and start the stack:"
echo "      docker compose build"
echo "      docker compose up -d"
echo ""
echo " 4. Verify the schema was applied:"
echo "      docker compose exec postgres psql -U postgres -d homemediahub_db -c '\dt'"
echo ""
echo " 5. Samba share is available at:"
echo "      Windows : \\\\$(hostname -I | awk '{print $1}')\\Photo"
echo "      macOS   : smb://$(hostname -I | awk '{print $1}')/Photo"
echo "================================================================"

# ── USB HDD mount note ────────────────────────────────────────────────────────
# If the USB HDD is not yet mounted, find its device name and add it to fstab:
#
#   lsblk -f                          # find the device (e.g. /dev/sdb1)
#   sudo blkid /dev/sdb1              # copy the UUID
#
#   # Add to /etc/fstab for auto-mount on boot:
#   UUID=<uuid>  /mnt/storage  ext4  defaults,noatime,nofail  0  2
#
#   sudo mount -a                     # mount now without rebooting
# ─────────────────────────────────────────────────────────────────────────────
