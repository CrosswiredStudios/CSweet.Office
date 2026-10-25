#!/bin/sh
# Installer-owned storage discovery and registration. No eval and no sourced registration files.

office_storage_platform() {
  case "$(uname -s)" in
    Linux) office_platform=linux; office_record=/etc/csweet/office-storage ;;
    Darwin) office_platform=macos; office_record=/Library/Preferences/com.csweet.office.storage ;;
    *) echo 'Unsupported Office storage platform.' >&2; return 2 ;;
  esac
}

office_safe_value() {
  # Record and discovery delimiters must never appear in paths/identities.
  case "$1" in *'|'*|*'
'*|*'\'*|*'"'*|*'%'*|*'`'*|*'$'*) return 1 ;; esac
  [ -n "$1" ]
}

office_list_volumes() {
  office_storage_platform
  if [ "$office_platform" = linux ]; then
    command -v jq >/dev/null || { echo 'Office storage discovery requires jq.' >&2; return 2; }
    findmnt --json -o TARGET,SOURCE,FSTYPE,OPTIONS,UUID | jq -r '
      .filesystems[] | recurse(.children[]?) |
      select(.fstype == "ext4" or .fstype == "xfs" or .fstype == "btrfs") |
      select(.uuid != null and .uuid != "") |
      [.uuid,.target,.source,.fstype,.options] | join("|")' |
    while IFS='|' read -r id mount source fs options; do
      office_safe_value "$mount" && office_safe_value "$id" || continue
      source=${source%%\[*}
      [ -b "$source" ] || continue
      reason=-
      backing=$(lsblk -s -J -o TRAN,RM,TYPE "$source" 2>/dev/null) || continue
      unsafe_backing=$(printf '%s' "$backing" | jq '[.. | objects | select(.rm == true or .rm == 1 or .rm == "1" or .type == "loop" or .type == "rom" or .tran == "usb" or .tran == "iscsi" or .tran == "mmc")] | length') || continue
      if [ "$unsafe_backing" -ne 0 ]; then
        reason='Removable and network-backed drives are not supported'
      fi
      case ",$options," in *,ro,*|*,noexec,*) reason='The volume must allow writes and service execution' ;; esac
      set -- $(df -Pk "$mount" | tail -n 1 | awk '{print $2, $4}')
      [ "$#" -eq 2 ] || continue
      total=$(( $1 * 1024 )); free=$(( $2 * 1024 ))
      label=$(lsblk -dn -o LABEL "$source" | tr '\n|' '  '); [ -n "$label" ] || label=-
      system=false; [ "$mount" != / ] || system=true
      printf '%s|%s|%s|%s|%s|%s|%s|%s\n' "$id" "$mount" "$label" "$fs" "$total" "$free" "$system" "$reason"
    done | awk -F '|' '!seen[$1]++'
  else
    # APFS's writable Data volume represents the system destination; sealed System,
    # Preboot, Recovery and VM mounts must not appear as independent choices.
    diskutil list -plist internal | plutil -extract AllDisks xml1 -o - - |
      sed -n 's/.*<string>\([^<]*\)<\/string>.*/\1/p' |
    while IFS= read -r disk; do
      info=$(diskutil info -plist "$disk" 2>/dev/null) || continue
      field() { printf '%s' "$info" | plutil -extract "$1" raw -o - - 2>/dev/null || true; }
      mount=$(field MountPoint); id=$(field VolumeUUID); fs=$(field FilesystemType)
      [ "$(field Internal)" = true ] || continue
      case "$fs" in apfs|hfs) ;; *) continue ;; esac
      case "$mount" in /System/Volumes/Data) mount=/ ;; /System/*|'') continue ;; esac
      office_safe_value "$mount" && office_safe_value "$id" || continue
      reason=-
      if [ "$(field ReadOnlyVolume)" = true ] || [ "$(field ReadOnlyMedia)" = true ]; then reason='The volume is read only'; fi
      if [ "$(field RemovableMedia)" = true ] || [ "$(field Ejectable)" = true ]; then reason='Removable drives are not supported'; fi
      if [ "$(field GlobalPermissionsEnabled)" = false ]; then reason='Enable filesystem ownership before installing Office'; fi
      set -- $(df -Pk "$mount" | tail -n 1 | awk '{print $2, $4}')
      [ "$#" -eq 2 ] || continue
      total=$(( $1 * 1024 )); free=$(( $2 * 1024 ))
      label=$(field VolumeName | tr '\n|' '  '); [ -n "$label" ] || label=-
      system=false; [ "$mount" != / ] || system=true
      printf '%s|%s|%s|%s|%s|%s|%s|%s\n' "$id" "$mount" "$label" "$fs" "$total" "$free" "$system" "$reason"
    done | awk -F '|' '!seen[$1]++'
  fi
}

office_set_roots() {
  if [ "$office_mount" = / ]; then
    if [ "$office_platform" = linux ]; then
      office_install_root=/opt/csweet/office; office_data_root=/var/lib/csweet/office; office_artifact_root=/var/lib/csweet/artifact-media
    else
      office_install_root='/Library/Application Support/CSweet/Office'; office_data_root=$office_install_root; office_artifact_root=$office_data_root/artifact-media
    fi
  else
    office_install_root=$office_mount/CSweet/Office/program
    office_data_root=$office_mount/CSweet/Office/data
    office_artifact_root=$office_data_root/artifact-media
  fi
}

office_assert_path() {
  path=$1
  case "$path" in */../*|*/./*|*/..|*/.) return 2 ;; /*) ;; *) echo 'Office paths must be absolute.' >&2; return 2 ;; esac
  office_safe_value "$path" || return 2
  while [ "$path" != / ]; do
    [ ! -L "$path" ] || { echo 'Office paths cannot contain symbolic links.' >&2; return 2; }
    if [ -e "$path" ]; then
      if [ "$office_platform" = linux ]; then meta=$(stat -c '%u %a' "$path"); else meta=$(stat -f '%u %Lp' "$path"); fi
      set -- $meta
      [ "$1" = 0 ] && [ $((0$2 & 0022)) -eq 0 ] || { echo 'Office storage ancestors must be owned by root and protected against other-user writes.' >&2; return 2; }
    fi
    path=$(dirname "$path")
  done
}

office_load_storage() {
  office_storage_platform
  [ -f "$office_record" ] || return 1
  office_assert_path "$office_record" || return 2
  [ "$(sed -n '1p' "$office_record")" = 1 ] || return 2
  office_volume_id=$(sed -n '2p' "$office_record")
  office_mount=$(sed -n '3p' "$office_record")
  saved_install=$(sed -n '4p' "$office_record"); saved_data=$(sed -n '5p' "$office_record")
  office_set_roots
  [ "$saved_install" = "$office_install_root" ] && [ "$saved_data" = "$office_data_root" ] || return 2
  if [ "${1:-}" = startup ] && [ "$office_platform" = linux ]; then
    # systemd deliberately exposes the host filesystem read-only except its configured
    # writable data paths. Check the mounted block identity without opening the device
    # or treating this service namespace's read-only root as an unsuitable host volume.
    office_verify_linux_mount || return 2
  else
    observed=$(office_list_volumes | awk -F '|' -v id="$office_volume_id" -v mount="$office_mount" '$1==id && $2==mount && $8=="-" {print $1}')
    [ "$observed" = "$office_volume_id" ] || { echo 'The Office volume is unavailable, replaced, or unsuitable.' >&2; return 2; }
  fi
  office_assert_path "$office_install_root" && office_assert_path "$office_data_root"
}

office_select_storage() {
  requested_volume=$1
  office_storage_platform
  if [ -e "$office_record" ] || [ -L "$office_record" ]; then
    office_load_storage || return 2
    [ -z "$requested_volume" ] || [ "$requested_volume" = "$office_volume_id" ] || { echo 'Moving an installed Office is not supported.' >&2; return 2; }
    return 0
  fi
  inventory=$(office_list_volumes) || return 2
  # Keep the existing 20 GiB installation/headroom reserve and 32 GiB workload minimum.
  eligible=$(printf '%s\n' "$inventory" | awk -F '|' '$8=="-" && $6>=55834574848')
  [ -n "$eligible" ] || { echo 'No fixed local volume has enough free space for Office.' >&2; return 2; }
  if [ -z "$requested_volume" ] && [ -t 0 ]; then
    printf '%s\n' "$inventory" | awk -F '|' '{printf "%s  %s (%s): %.1f GB free / %.1f GB total [%s]\n", $1,$2,$3,$6/1073741824,$5/1073741824,$8}' >&2
    if [ "$(printf '%s\n' "$eligible" | wc -l | tr -d ' ')" -gt 1 ]; then
      printf 'Office volume UUID (Enter for the system volume): ' >&2
      IFS= read -r requested_volume
    fi
  fi
  if [ -z "$requested_volume" ]; then
    requested_volume=$(printf '%s\n' "$eligible" | awk -F '|' '$7=="true" {print $1; exit}')
    if [ -z "$requested_volume" ] && [ "$(printf '%s\n' "$eligible" | wc -l | tr -d ' ')" = 1 ]; then requested_volume=${eligible%%|*}; fi
  fi
  selected=$(printf '%s\n' "$eligible" | awk -F '|' -v id="$requested_volume" '$1==id {print; exit}')
  [ -n "$selected" ] || { echo 'Choose an eligible Office volume with --storage-volume UUID.' >&2; return 2; }
  office_volume_id=${selected%%|*}; rest=${selected#*|}; office_mount=${rest%%|*}
  office_set_roots
  office_assert_path "$office_install_root" && office_assert_path "$office_data_root" || return 2
  printf 'Office services: %s\nOffice data: %s\n' "$office_install_root" "$office_data_root" >&2
}

office_save_storage() {
  office_assert_path "$(dirname "$office_record")" || return 2
  mkdir -p "$(dirname "$office_record")"
  temporary=$(mktemp "$office_record.XXXXXX") || return 2
  printf '1\n%s\n%s\n%s\n%s\n' "$office_volume_id" "$office_mount" "$office_install_root" "$office_data_root" > "$temporary"
  chmod 0644 "$temporary"
  mv -f "$temporary" "$office_record"
}

office_sed_escape() { printf '%s' "$1" | sed 's/[&|\\]/\\&/g'; }

office_render_unit() {
  sed -e "s|__OFFICE_INSTALL_ROOT__|$(office_sed_escape "$office_install_root")|g" \
      -e "s|__OFFICE_DATA_ROOT__|$(office_sed_escape "$office_data_root")|g" \
      -e "s|__OFFICE_ARTIFACT_ROOT__|$(office_sed_escape "$office_artifact_root")|g" "$1" > "$2"
  printf '\n[Unit]\nRequiresMountsFor="%s" "%s"\nConditionPathIsMountPoint="%s"\n' "$office_install_root" "$office_data_root" "$office_mount" >> "$2"
}

office_xml_escape() { printf '%s' "$1" | sed 's/\&/\&amp;/g; s/</\&lt;/g; s/>/\&gt;/g'; }

office_render_plist() {
  sed -e "s|__OFFICE_INSTALL_ROOT__|$(office_sed_escape "$(office_xml_escape "$office_install_root")")|g" \
      -e "s|__OFFICE_DATA_ROOT__|$(office_sed_escape "$(office_xml_escape "$office_data_root")")|g" \
      -e "s|__CONTROL_PLANE_URL__|$(office_sed_escape "$(office_xml_escape "$control_plane")")|g" \
      -e "s|__SECURITY_PROFILE__|$security_profile|g" \
      -e "s|__MIXED_USE_HOST__|$mixed_use|g" \
      -e "s|__ALLOW_DEVELOPMENT_ASSIGNMENTS__|$allow_development|g" "$1" > "$2"
}

office_verify_linux_mount() {
  case "$office_volume_id" in ''|*[!a-zA-Z0-9-]*) return 2 ;; esac
  expected_device=$(readlink -f "/dev/disk/by-uuid/$office_volume_id") || return 2
  actual_source=$(findmnt -rn -o SOURCE --target "$office_mount") || return 2
  actual_source=${actual_source%%\[*}
  actual_device=$(readlink -f "$actual_source") || return 2
  [ -n "$expected_device" ] && [ "$actual_device" = "$expected_device" ] || {
    echo 'The registered Office volume is missing or has been replaced.' >&2
    return 2
  }
}
