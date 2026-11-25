# Installed by protondrive (/etc/profile.d/protondrive.sh).
# When a user opens an interactive login shell (a terminal, SSH), lists their synced folders that need attention,
# such as a paused sync or deletions waiting for confirmation. Prints nothing when everything is fine.
# It only reads small files in the user's own ~/.local/state/protondrive/attention/.

case $- in
    *i*)
        _protondrive_dir="${XDG_STATE_HOME:-${HOME:-/nonexistent}/.local/state}/protondrive/attention"
        if [ -d "$_protondrive_dir" ]; then
            _protondrive_header=1
            for _protondrive_file in "$_protondrive_dir"/*; do
                [ -f "$_protondrive_file" ] || continue
                if [ "$_protondrive_header" = 1 ]; then
                    printf '\nprotondrive: synced folders need attention (details: protondrive sync status)\n'
                    _protondrive_header=0
                fi
                cat "$_protondrive_file"
            done
            [ "$_protondrive_header" = 0 ] && printf '\n'
        fi
        unset _protondrive_dir _protondrive_header _protondrive_file
        ;;
esac
