//! Keyboard translation from winit events to terminal input bytes.
//!
//! This is the DEC/xterm baseline: enough to drive a shell and a full-screen
//! application. The Kitty keyboard protocol that cmux-tui negotiates over a
//! real TTY is not modelled here yet.

use winit::event::KeyEvent;
use winit::keyboard::{Key, ModifiersState, NamedKey};

/// Bytes to send to the PTY for a key press, or `None` if the key produces no
/// input (modifier presses, unhandled named keys).
pub fn encode(event: &KeyEvent, mods: ModifiersState) -> Option<Vec<u8>> {
    let ctrl = mods.control_key();
    let alt = mods.alt_key();

    let base: Vec<u8> = match &event.logical_key {
        Key::Named(named) => match named {
            NamedKey::Enter => vec![b'\r'],
            NamedKey::Backspace => vec![0x7f],
            NamedKey::Tab => vec![b'\t'],
            NamedKey::Escape => vec![0x1b],
            NamedKey::ArrowUp => b"\x1b[A".to_vec(),
            NamedKey::ArrowDown => b"\x1b[B".to_vec(),
            NamedKey::ArrowRight => b"\x1b[C".to_vec(),
            NamedKey::ArrowLeft => b"\x1b[D".to_vec(),
            NamedKey::Home => b"\x1b[H".to_vec(),
            NamedKey::End => b"\x1b[F".to_vec(),
            NamedKey::Insert => b"\x1b[2~".to_vec(),
            NamedKey::Delete => b"\x1b[3~".to_vec(),
            NamedKey::PageUp => b"\x1b[5~".to_vec(),
            NamedKey::PageDown => b"\x1b[6~".to_vec(),
            NamedKey::F1 => b"\x1bOP".to_vec(),
            NamedKey::F2 => b"\x1bOQ".to_vec(),
            NamedKey::F3 => b"\x1bOR".to_vec(),
            NamedKey::F4 => b"\x1bOS".to_vec(),
            NamedKey::Space => vec![b' '],
            _ => return None,
        },
        Key::Character(text) => {
            if ctrl {
                // Ctrl-A..Ctrl-Z and the handful of punctuation controls.
                let c = text.chars().next()?.to_ascii_lowercase();
                let code = match c {
                    'a'..='z' => c as u8 - b'a' + 1,
                    '[' => 0x1b,
                    '\\' => 0x1c,
                    ']' => 0x1d,
                    '@' | ' ' => 0x00,
                    _ => return None,
                };
                vec![code]
            } else {
                text.as_bytes().to_vec()
            }
        }
        _ => event.text.as_ref().map(|t| t.as_bytes().to_vec())?,
    };

    // Alt sends ESC before the sequence, which is what xterm calls meta-prefix.
    if alt && !base.starts_with(&[0x1b]) {
        let mut out = Vec::with_capacity(base.len() + 1);
        out.push(0x1b);
        out.extend_from_slice(&base);
        return Some(out);
    }
    Some(base)
}
