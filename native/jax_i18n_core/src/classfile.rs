use std::fmt;

use crate::model::{ClassFileSummary, ClassStringReference};

const CLASSFILE_MAGIC: u32 = 0xcafe_babe;
const MIN_CLASSFILE_MAJOR_VERSION: u16 = 45;
const MAX_CODE_LENGTH: u64 = 65_535;

#[derive(Debug)]
pub(crate) struct ParsedClass {
    pub summary: ClassFileSummary,
    pub references: Vec<ClassStringReference>,
}

#[derive(Debug)]
pub(crate) struct ClassParseError {
    offset: usize,
    message: String,
}

impl ClassParseError {
    fn new(offset: usize, message: impl Into<String>) -> Self {
        Self {
            offset,
            message: message.into(),
        }
    }
}

impl fmt::Display for ClassParseError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            formatter,
            "classfile parse error at byte {}: {}",
            self.offset, self.message
        )
    }
}

#[derive(Debug)]
enum ConstantPoolEntry {
    Unusable,
    Utf8(String),
    Class(u16),
    String(u16),
    Other,
}

#[derive(Debug)]
struct Reader<'a> {
    bytes: &'a [u8],
    position: usize,
}

impl<'a> Reader<'a> {
    const fn new(bytes: &'a [u8]) -> Self {
        Self { bytes, position: 0 }
    }

    fn read_u8(&mut self, context: &'static str) -> Result<u8, ClassParseError> {
        let bytes = self.take(1, context)?;
        Ok(bytes[0])
    }

    fn read_u16(&mut self, context: &'static str) -> Result<u16, ClassParseError> {
        let bytes = self.take(2, context)?;
        Ok(u16::from_be_bytes([bytes[0], bytes[1]]))
    }

    fn read_u32(&mut self, context: &'static str) -> Result<u32, ClassParseError> {
        let bytes = self.take(4, context)?;
        Ok(u32::from_be_bytes([bytes[0], bytes[1], bytes[2], bytes[3]]))
    }

    fn take(&mut self, length: usize, context: &'static str) -> Result<&'a [u8], ClassParseError> {
        let end = self
            .position
            .checked_add(length)
            .ok_or_else(|| ClassParseError::new(self.position, "offset overflow"))?;
        if end > self.bytes.len() {
            return Err(ClassParseError::new(
                self.position,
                format!("truncated {context}: need {length} bytes"),
            ));
        }
        let result = &self.bytes[self.position..end];
        self.position = end;
        Ok(result)
    }

    fn skip(&mut self, length: usize, context: &'static str) -> Result<(), ClassParseError> {
        self.take(length, context).map(|_| ())
    }

    fn finish(&self, context: &'static str) -> Result<(), ClassParseError> {
        if self.position == self.bytes.len() {
            Ok(())
        } else {
            Err(ClassParseError::new(
                self.position,
                format!("unexpected trailing bytes in {context}"),
            ))
        }
    }
}

pub(crate) fn parse_classfile(
    bytes: &[u8],
    archive_index: u64,
    archive_path: &str,
) -> Result<ParsedClass, ClassParseError> {
    let mut reader = Reader::new(bytes);
    let magic = reader.read_u32("classfile magic")?;
    if magic != CLASSFILE_MAGIC {
        return Err(ClassParseError::new(0, "missing CAFEBABE magic"));
    }
    let minor_version = reader.read_u16("minor version")?;
    let major_version = reader.read_u16("major version")?;
    if major_version < MIN_CLASSFILE_MAJOR_VERSION {
        return Err(ClassParseError::new(
            6,
            format!("unsupported classfile major version {major_version}"),
        ));
    }

    let constant_pool = parse_constant_pool(&mut reader)?;
    reader.skip(2, "class access flags")?;
    let this_class_index = reader.read_u16("this_class index")?;
    reader.skip(2, "super_class index")?;
    let class_name = resolve_class_name(&constant_pool, this_class_index)?.to_owned();

    let interface_count = usize::from(reader.read_u16("interfaces count")?);
    let interface_bytes = interface_count
        .checked_mul(2)
        .ok_or_else(|| ClassParseError::new(reader.position, "interfaces size overflow"))?;
    reader.skip(interface_bytes, "interfaces")?;

    let field_count = usize::from(reader.read_u16("fields count")?);
    for _ in 0..field_count {
        skip_member(&mut reader, &constant_pool, "field")?;
    }

    let method_count = usize::from(reader.read_u16("methods count")?);
    let mut references = Vec::new();
    for _ in 0..method_count {
        parse_method(
            &mut reader,
            &constant_pool,
            archive_index,
            archive_path,
            &class_name,
            &mut references,
        )?;
    }

    let class_attribute_count = reader.read_u16("class attributes count")?;
    skip_attributes(
        &mut reader,
        &constant_pool,
        class_attribute_count,
        "class attribute",
    )?;
    reader.finish("classfile")?;

    Ok(ParsedClass {
        summary: ClassFileSummary {
            archive_index,
            archive_path: archive_path.to_owned(),
            class: class_name,
            minor_version,
            major_version,
            string_reference_count: u64::try_from(references.len()).unwrap_or(u64::MAX),
        },
        references,
    })
}

fn parse_constant_pool(reader: &mut Reader<'_>) -> Result<Vec<ConstantPoolEntry>, ClassParseError> {
    let count = usize::from(reader.read_u16("constant_pool_count")?);
    if count == 0 {
        return Err(ClassParseError::new(
            reader.position.saturating_sub(2),
            "constant_pool_count cannot be zero",
        ));
    }

    let mut entries = Vec::with_capacity(count);
    entries.push(ConstantPoolEntry::Unusable);
    let mut index = 1_usize;
    while index < count {
        let tag_offset = reader.position;
        let tag = reader.read_u8("constant-pool tag")?;
        let entry = match tag {
            1 => {
                let length = usize::from(reader.read_u16("CONSTANT_Utf8 length")?);
                let value_offset = reader.position;
                let encoded = reader.take(length, "CONSTANT_Utf8 bytes")?;
                ConstantPoolEntry::Utf8(decode_modified_utf8(encoded).map_err(|message| {
                    ClassParseError::new(value_offset, format!("invalid modified UTF-8: {message}"))
                })?)
            }
            3 | 4 => {
                reader.skip(4, "integer/float constant")?;
                ConstantPoolEntry::Other
            }
            5 | 6 => {
                if index + 1 >= count {
                    return Err(ClassParseError::new(
                        tag_offset,
                        "long/double constant has no required second pool slot",
                    ));
                }
                reader.skip(8, "long/double constant")?;
                entries.push(ConstantPoolEntry::Other);
                entries.push(ConstantPoolEntry::Unusable);
                index += 2;
                continue;
            }
            7 => ConstantPoolEntry::Class(reader.read_u16("CONSTANT_Class name index")?),
            8 => ConstantPoolEntry::String(reader.read_u16("CONSTANT_String value index")?),
            9..=11 => {
                reader.skip(4, "member reference constant")?;
                ConstantPoolEntry::Other
            }
            12 => {
                reader.skip(4, "name-and-type constant")?;
                ConstantPoolEntry::Other
            }
            15 => {
                reader.skip(3, "method-handle constant")?;
                ConstantPoolEntry::Other
            }
            16 | 19 | 20 => {
                reader.skip(2, "single-index constant")?;
                ConstantPoolEntry::Other
            }
            17 | 18 => {
                reader.skip(4, "dynamic constant")?;
                ConstantPoolEntry::Other
            }
            _ => {
                return Err(ClassParseError::new(
                    tag_offset,
                    format!("unknown constant-pool tag {tag}"),
                ));
            }
        };
        entries.push(entry);
        index += 1;
    }
    debug_assert_eq!(entries.len(), count);
    Ok(entries)
}

fn skip_member(
    reader: &mut Reader<'_>,
    constant_pool: &[ConstantPoolEntry],
    context: &'static str,
) -> Result<(), ClassParseError> {
    reader.skip(2, "member access flags")?;
    let name_index = reader.read_u16("member name index")?;
    let descriptor_index = reader.read_u16("member descriptor index")?;
    resolve_utf8(constant_pool, name_index, "member name")?;
    resolve_utf8(constant_pool, descriptor_index, "member descriptor")?;
    let attribute_count = reader.read_u16("member attributes count")?;
    skip_attributes(reader, constant_pool, attribute_count, context)
}

fn parse_method(
    reader: &mut Reader<'_>,
    constant_pool: &[ConstantPoolEntry],
    archive_index: u64,
    archive_path: &str,
    class_name: &str,
    references: &mut Vec<ClassStringReference>,
) -> Result<(), ClassParseError> {
    reader.skip(2, "method access flags")?;
    let name_index = reader.read_u16("method name index")?;
    let descriptor_index = reader.read_u16("method descriptor index")?;
    let method_name = resolve_utf8(constant_pool, name_index, "method name")?.to_owned();
    let descriptor = resolve_utf8(constant_pool, descriptor_index, "method descriptor")?.to_owned();
    let attribute_count = reader.read_u16("method attributes count")?;

    for _ in 0..attribute_count {
        let attribute_name_index = reader.read_u16("method attribute name index")?;
        let attribute_length = reader.read_u32("method attribute length")?;
        let attribute_name =
            resolve_utf8(constant_pool, attribute_name_index, "method attribute name")?;
        let attribute_bytes = reader.take(
            usize::try_from(attribute_length).map_err(|_| {
                ClassParseError::new(reader.position, "attribute length does not fit memory")
            })?,
            "method attribute body",
        )?;
        if attribute_name == "Code" {
            parse_code_attribute(
                attribute_bytes,
                constant_pool,
                archive_index,
                archive_path,
                class_name,
                &method_name,
                &descriptor,
                references,
            )?;
        }
    }
    Ok(())
}

#[allow(clippy::too_many_arguments)]
fn parse_code_attribute(
    bytes: &[u8],
    constant_pool: &[ConstantPoolEntry],
    archive_index: u64,
    archive_path: &str,
    class_name: &str,
    method_name: &str,
    descriptor: &str,
    references: &mut Vec<ClassStringReference>,
) -> Result<(), ClassParseError> {
    let mut reader = Reader::new(bytes);
    reader.skip(2, "Code max_stack")?;
    reader.skip(2, "Code max_locals")?;
    let code_length = u64::from(reader.read_u32("Code length")?);
    if code_length == 0 || code_length > MAX_CODE_LENGTH {
        return Err(ClassParseError::new(
            reader.position.saturating_sub(4),
            format!("Code length must be between 1 and {MAX_CODE_LENGTH}"),
        ));
    }
    let code = reader.take(
        usize::try_from(code_length)
            .map_err(|_| ClassParseError::new(reader.position, "Code length overflow"))?,
        "Code bytecode",
    )?;
    scan_bytecode(
        code,
        constant_pool,
        archive_index,
        archive_path,
        class_name,
        method_name,
        descriptor,
        references,
    )?;

    let exception_count = usize::from(reader.read_u16("exception table length")?);
    let exception_bytes = exception_count
        .checked_mul(8)
        .ok_or_else(|| ClassParseError::new(reader.position, "exception table size overflow"))?;
    reader.skip(exception_bytes, "exception table")?;
    let nested_attribute_count = reader.read_u16("Code attributes count")?;
    skip_attributes(
        &mut reader,
        constant_pool,
        nested_attribute_count,
        "Code attribute",
    )?;
    reader.finish("Code attribute")
}

fn skip_attributes(
    reader: &mut Reader<'_>,
    constant_pool: &[ConstantPoolEntry],
    count: u16,
    context: &'static str,
) -> Result<(), ClassParseError> {
    for _ in 0..count {
        let name_index = reader.read_u16("attribute name index")?;
        resolve_utf8(constant_pool, name_index, "attribute name")?;
        let length = reader.read_u32("attribute length")?;
        reader.skip(
            usize::try_from(length).map_err(|_| {
                ClassParseError::new(reader.position, "attribute length does not fit memory")
            })?,
            context,
        )?;
    }
    Ok(())
}

#[allow(clippy::too_many_arguments)]
fn scan_bytecode(
    code: &[u8],
    constant_pool: &[ConstantPoolEntry],
    archive_index: u64,
    archive_path: &str,
    class_name: &str,
    method_name: &str,
    descriptor: &str,
    references: &mut Vec<ClassStringReference>,
) -> Result<(), ClassParseError> {
    let mut offset = 0_usize;
    while offset < code.len() {
        let opcode = code[offset];
        let length = match opcode {
            0xaa => tableswitch_length(code, offset)?,
            0xab => lookupswitch_length(code, offset)?,
            0xc4 => wide_length(code, offset)?,
            _ => fixed_instruction_length(opcode).ok_or_else(|| {
                ClassParseError::new(offset, format!("reserved or unknown opcode 0x{opcode:02x}"))
            })?,
        };
        let end = offset
            .checked_add(length)
            .ok_or_else(|| ClassParseError::new(offset, "instruction length overflow"))?;
        if end > code.len() {
            return Err(ClassParseError::new(
                offset,
                format!("truncated opcode 0x{opcode:02x}"),
            ));
        }

        let (constant_pool_index, mnemonic) = match opcode {
            0x12 => (Some(u16::from(code[offset + 1])), "ldc"),
            0x13 => (
                Some(u16::from_be_bytes([code[offset + 1], code[offset + 2]])),
                "ldc_w",
            ),
            _ => (None, ""),
        };
        if let Some(constant_pool_index) = constant_pool_index {
            let entry = resolve_entry(constant_pool, constant_pool_index, "LDC constant")?;
            if let ConstantPoolEntry::String(value_index) = entry {
                let value = resolve_utf8(constant_pool, *value_index, "string constant")?;
                let rejected_reason = rejected_reason(value);
                references.push(ClassStringReference {
                    archive_index,
                    archive_path: archive_path.to_owned(),
                    class: class_name.to_owned(),
                    method: method_name.to_owned(),
                    descriptor: descriptor.to_owned(),
                    bytecode_offset: u64::try_from(offset).unwrap_or(u64::MAX),
                    opcode: mnemonic,
                    value: value.to_owned(),
                    constant_pool_index,
                    candidate: rejected_reason.is_none(),
                    rejected_reason,
                });
            }
        }

        offset = end;
    }
    Ok(())
}

const fn fixed_instruction_length(opcode: u8) -> Option<usize> {
    match opcode {
        0x00..=0x0f
        | 0x1a..=0x35
        | 0x3b..=0x83
        | 0x85..=0x98
        | 0xac..=0xb1
        | 0xbe..=0xbf
        | 0xc2..=0xc3 => Some(1),
        0x10 | 0x12 | 0x15..=0x19 | 0x36..=0x3a | 0xa9 | 0xbc => Some(2),
        0x11
        | 0x13..=0x14
        | 0x84
        | 0x99..=0xa8
        | 0xb2..=0xb8
        | 0xbb
        | 0xbd
        | 0xc0..=0xc1
        | 0xc6..=0xc7 => Some(3),
        0xc5 => Some(4),
        0xb9..=0xba | 0xc8..=0xc9 => Some(5),
        _ => None,
    }
}

fn wide_length(code: &[u8], offset: usize) -> Result<usize, ClassParseError> {
    let modified = *code
        .get(offset + 1)
        .ok_or_else(|| ClassParseError::new(offset, "truncated wide opcode"))?;
    match modified {
        0x15..=0x19 | 0x36..=0x3a | 0xa9 => Ok(4),
        0x84 => Ok(6),
        _ => Err(ClassParseError::new(
            offset,
            format!("invalid opcode 0x{modified:02x} after wide"),
        )),
    }
}

fn tableswitch_length(code: &[u8], offset: usize) -> Result<usize, ClassParseError> {
    let padding = (4 - ((offset + 1) % 4)) % 4;
    let header = offset
        .checked_add(1 + padding)
        .ok_or_else(|| ClassParseError::new(offset, "tableswitch offset overflow"))?;
    let low = read_code_i32(code, header + 4, offset, "tableswitch low")?;
    let high = read_code_i32(code, header + 8, offset, "tableswitch high")?;
    if high < low {
        return Err(ClassParseError::new(
            offset,
            "tableswitch high is below low",
        ));
    }
    let entry_count = i64::from(high) - i64::from(low) + 1;
    let jump_bytes = usize::try_from(entry_count)
        .ok()
        .and_then(|count| count.checked_mul(4))
        .ok_or_else(|| ClassParseError::new(offset, "tableswitch size overflow"))?;
    1_usize
        .checked_add(padding)
        .and_then(|length| length.checked_add(12))
        .and_then(|length| length.checked_add(jump_bytes))
        .ok_or_else(|| ClassParseError::new(offset, "tableswitch length overflow"))
}

fn lookupswitch_length(code: &[u8], offset: usize) -> Result<usize, ClassParseError> {
    let padding = (4 - ((offset + 1) % 4)) % 4;
    let header = offset
        .checked_add(1 + padding)
        .ok_or_else(|| ClassParseError::new(offset, "lookupswitch offset overflow"))?;
    // Reading default also proves the first four header bytes are present.
    read_code_i32(code, header, offset, "lookupswitch default")?;
    let pair_count = read_code_i32(code, header + 4, offset, "lookupswitch npairs")?;
    if pair_count < 0 {
        return Err(ClassParseError::new(
            offset,
            "lookupswitch npairs is negative",
        ));
    }
    let pair_bytes = usize::try_from(pair_count)
        .ok()
        .and_then(|count| count.checked_mul(8))
        .ok_or_else(|| ClassParseError::new(offset, "lookupswitch size overflow"))?;
    1_usize
        .checked_add(padding)
        .and_then(|length| length.checked_add(8))
        .and_then(|length| length.checked_add(pair_bytes))
        .ok_or_else(|| ClassParseError::new(offset, "lookupswitch length overflow"))
}

fn read_code_i32(
    code: &[u8],
    position: usize,
    opcode_offset: usize,
    context: &'static str,
) -> Result<i32, ClassParseError> {
    let end = position
        .checked_add(4)
        .ok_or_else(|| ClassParseError::new(opcode_offset, "bytecode offset overflow"))?;
    let bytes = code
        .get(position..end)
        .ok_or_else(|| ClassParseError::new(opcode_offset, format!("truncated {context}")))?;
    Ok(i32::from_be_bytes([bytes[0], bytes[1], bytes[2], bytes[3]]))
}

fn resolve_entry<'a>(
    constant_pool: &'a [ConstantPoolEntry],
    index: u16,
    context: &'static str,
) -> Result<&'a ConstantPoolEntry, ClassParseError> {
    if index == 0 {
        return Err(ClassParseError::new(
            0,
            format!("{context} references constant-pool index zero"),
        ));
    }
    match constant_pool.get(usize::from(index)) {
        Some(ConstantPoolEntry::Unusable) | None => Err(ClassParseError::new(
            0,
            format!("{context} references invalid constant-pool index {index}"),
        )),
        Some(entry) => Ok(entry),
    }
}

fn resolve_utf8<'a>(
    constant_pool: &'a [ConstantPoolEntry],
    index: u16,
    context: &'static str,
) -> Result<&'a str, ClassParseError> {
    match resolve_entry(constant_pool, index, context)? {
        ConstantPoolEntry::Utf8(value) => Ok(value),
        _ => Err(ClassParseError::new(
            0,
            format!("{context} index {index} is not CONSTANT_Utf8"),
        )),
    }
}

fn resolve_class_name(
    constant_pool: &[ConstantPoolEntry],
    index: u16,
) -> Result<&str, ClassParseError> {
    match resolve_entry(constant_pool, index, "this_class")? {
        ConstantPoolEntry::Class(name_index) => {
            resolve_utf8(constant_pool, *name_index, "class name")
        }
        _ => Err(ClassParseError::new(0, "this_class is not CONSTANT_Class")),
    }
}

fn decode_modified_utf8(bytes: &[u8]) -> Result<String, &'static str> {
    let mut units = Vec::with_capacity(bytes.len());
    let mut index = 0_usize;
    while index < bytes.len() {
        let first = bytes[index];
        match first {
            0x01..=0x7f => {
                units.push(u16::from(first));
                index += 1;
            }
            0x00 => return Err("literal NUL byte is not permitted"),
            0xc0..=0xdf => {
                let second = *bytes.get(index + 1).ok_or("truncated two-byte sequence")?;
                if second & 0xc0 != 0x80 {
                    return Err("invalid continuation byte");
                }
                let unit = (u16::from(first & 0x1f) << 6) | u16::from(second & 0x3f);
                if first == 0xc0 && second == 0x80 {
                    units.push(0);
                } else if unit < 0x80 {
                    return Err("overlong two-byte sequence");
                } else {
                    units.push(unit);
                }
                index += 2;
            }
            0xe0..=0xef => {
                let second = *bytes
                    .get(index + 1)
                    .ok_or("truncated three-byte sequence")?;
                let third = *bytes
                    .get(index + 2)
                    .ok_or("truncated three-byte sequence")?;
                if second & 0xc0 != 0x80 || third & 0xc0 != 0x80 {
                    return Err("invalid continuation byte");
                }
                let unit = (u16::from(first & 0x0f) << 12)
                    | (u16::from(second & 0x3f) << 6)
                    | u16::from(third & 0x3f);
                if unit < 0x800 {
                    return Err("overlong three-byte sequence");
                }
                units.push(unit);
                index += 3;
            }
            _ => return Err("four-byte UTF-8 is not valid modified UTF-8"),
        }
    }
    String::from_utf16(&units).map_err(|_| "unpaired UTF-16 surrogate")
}

fn rejected_reason(value: &str) -> Option<&'static str> {
    let trimmed = value.trim();
    if trimmed.is_empty() {
        return Some("empty_or_whitespace");
    }
    if trimmed.chars().count() == 1 {
        return Some("single_character");
    }
    if value
        .chars()
        .any(|character| character.is_control() && !matches!(character, '\n' | '\r' | '\t'))
    {
        return Some("contains_control_characters");
    }
    if value.len() > 4_096 {
        return Some("too_long_for_ui_text");
    }
    if !value.chars().any(char::is_alphabetic) {
        return Some("no_alphabetic_characters");
    }

    let ascii_lower = trimmed.to_ascii_lowercase();
    if ascii_lower.contains("://")
        || ascii_lower.starts_with("file:")
        || ascii_lower.starts_with("jar:")
    {
        return Some("likely_uri");
    }
    if looks_like_jvm_descriptor(trimmed) {
        return Some("likely_jvm_descriptor");
    }
    if !trimmed.chars().any(char::is_whitespace)
        && (trimmed.contains('/') || trimmed.contains('\\'))
    {
        return Some("likely_path_or_internal_name");
    }
    if !trimmed.chars().any(char::is_whitespace)
        && (trimmed.contains('.') || trimmed.contains(':') || trimmed.contains('_'))
        && trimmed.chars().all(|character| {
            character.is_ascii_alphanumeric()
                || matches!(character, '.' | ':' | '_' | '-' | '/' | '$')
        })
    {
        return Some("likely_identifier_or_translation_key");
    }
    None
}

fn looks_like_jvm_descriptor(value: &str) -> bool {
    if value.starts_with('(') && value.contains(')') && !value.chars().any(char::is_whitespace) {
        return true;
    }
    if value.starts_with('L') && value.ends_with(';') && value.contains('/') {
        return true;
    }
    let without_arrays = value.trim_start_matches('[');
    value.starts_with('[')
        && (matches!(
            without_arrays,
            "B" | "C" | "D" | "F" | "I" | "J" | "S" | "Z"
        ) || (without_arrays.starts_with('L') && without_arrays.ends_with(';')))
}

#[cfg(test)]
mod tests {
    use super::{decode_modified_utf8, parse_classfile};

    #[test]
    fn parses_ldc_and_ldc_w_with_long_double_slot_accounting() {
        let bytes = fixture_class(&[0x12, 11, 0xb0], &[0x13, 0x00, 13, 0xb0]);
        let parsed = parse_classfile(&bytes, 7, "example/Test.class").unwrap();
        assert_eq!(parsed.summary.class, "example/Test");
        assert_eq!(parsed.summary.major_version, 61);
        assert_eq!(parsed.references.len(), 2);

        let first = &parsed.references[0];
        assert_eq!(first.method, "message");
        assert_eq!(first.descriptor, "()Ljava/lang/String;");
        assert_eq!(first.bytecode_offset, 0);
        assert_eq!(first.constant_pool_index, 11);
        assert_eq!(first.value, "Hello player!");
        assert!(first.candidate);

        let second = &parsed.references[1];
        assert_eq!(second.method, "keyMessage");
        assert_eq!(second.opcode, "ldc_w");
        assert_eq!(second.constant_pool_index, 13);
        assert!(!second.candidate);
        assert_eq!(
            second.rejected_reason,
            Some("likely_identifier_or_translation_key")
        );
    }

    #[test]
    fn decodes_modified_utf8_nul_and_surrogate_pair() {
        assert_eq!(decode_modified_utf8(&[0xc0, 0x80]).unwrap(), "\0");
        assert_eq!(
            decode_modified_utf8(&[0xed, 0xa0, 0xbd, 0xed, 0xb8, 0x80]).unwrap(),
            "😀"
        );
    }

    #[test]
    fn decodes_variable_length_instructions_on_real_boundaries() {
        let mut table_code = vec![0xaa, 0, 0, 0];
        table_code.extend_from_slice(&0_i32.to_be_bytes()); // default
        table_code.extend_from_slice(&0_i32.to_be_bytes()); // low
        table_code.extend_from_slice(&0_i32.to_be_bytes()); // high
        table_code.extend_from_slice(&0x1200_0000_i32.to_be_bytes()); // jump bytes contain 0x12
        table_code.extend_from_slice(&[0x12, 11, 0xb0]);
        let parsed =
            parse_classfile(&fixture_class(&table_code, &[0xb0]), 0, "Table.class").unwrap();
        assert_eq!(parsed.references.len(), 1);
        assert_eq!(parsed.references[0].bytecode_offset, 20);

        let mut lookup_code = vec![0xab, 0, 0, 0];
        lookup_code.extend_from_slice(&0_i32.to_be_bytes()); // default
        lookup_code.extend_from_slice(&1_i32.to_be_bytes()); // npairs
        lookup_code.extend_from_slice(&0x1200_0000_i32.to_be_bytes()); // match contains 0x12
        lookup_code.extend_from_slice(&0_i32.to_be_bytes()); // jump
        lookup_code.extend_from_slice(&[0x12, 11, 0xb0]);
        let parsed =
            parse_classfile(&fixture_class(&lookup_code, &[0xb0]), 0, "Lookup.class").unwrap();
        assert_eq!(parsed.references.len(), 1);
        assert_eq!(parsed.references[0].bytecode_offset, 20);

        let wide_code = [0xc4, 0x15, 0x00, 0x01, 0x12, 11, 0xb0];
        let parsed = parse_classfile(&fixture_class(&wide_code, &[0xb0]), 0, "Wide.class").unwrap();
        assert_eq!(parsed.references.len(), 1);
        assert_eq!(parsed.references[0].bytecode_offset, 4);
    }

    #[test]
    fn rejects_bad_magic_truncation_invalid_slots_and_bad_bytecode() {
        assert!(parse_classfile(b"not a class", 0, "Bad.class").is_err());

        let mut truncated = fixture_class(&[0x12, 11, 0xb0], &[0x13, 0, 13, 0xb0]);
        truncated.truncate(truncated.len() - 3);
        assert!(parse_classfile(&truncated, 0, "Truncated.class").is_err());

        let mut invalid_slot = Vec::new();
        push_u32(&mut invalid_slot, 0xcafe_babe);
        push_u16(&mut invalid_slot, 0);
        push_u16(&mut invalid_slot, 61);
        push_u16(&mut invalid_slot, 2);
        invalid_slot.push(5);
        invalid_slot.extend_from_slice(&0_u64.to_be_bytes());
        assert!(parse_classfile(&invalid_slot, 0, "Slot.class").is_err());

        let malformed_code = fixture_class(&[0xaa], &[0xb0]);
        assert!(parse_classfile(&malformed_code, 0, "Switch.class").is_err());

        let valid = fixture_class(&[0x12, 11, 0xb0], &[0xb0]);
        for length in 0..valid.len() {
            assert!(
                parse_classfile(&valid[..length], 0, "EveryTruncation.class").is_err(),
                "accepted truncation at length {length}"
            );
        }
    }

    fn fixture_class(first_code: &[u8], second_code: &[u8]) -> Vec<u8> {
        let mut bytes = Vec::new();
        push_u32(&mut bytes, 0xcafe_babe);
        push_u16(&mut bytes, 0);
        push_u16(&mut bytes, 61);
        push_u16(&mut bytes, 15); // constant_pool_count
        push_utf8(&mut bytes, "example/Test"); // #1
        push_class(&mut bytes, 1); // #2
        push_utf8(&mut bytes, "java/lang/Object"); // #3
        push_class(&mut bytes, 3); // #4
        push_utf8(&mut bytes, "message"); // #5
        push_utf8(&mut bytes, "()Ljava/lang/String;"); // #6
        push_utf8(&mut bytes, "Code"); // #7
        bytes.push(5); // #8 long, #9 unusable
        bytes.extend_from_slice(&0_u64.to_be_bytes());
        push_utf8(&mut bytes, "Hello player!"); // #10
        push_string(&mut bytes, 10); // #11
        push_utf8(&mut bytes, "example.translation.key"); // #12
        push_string(&mut bytes, 12); // #13
        push_utf8(&mut bytes, "keyMessage"); // #14

        push_u16(&mut bytes, 0x0021); // public, super
        push_u16(&mut bytes, 2); // this_class
        push_u16(&mut bytes, 4); // super_class
        push_u16(&mut bytes, 0); // interfaces
        push_u16(&mut bytes, 0); // fields
        push_u16(&mut bytes, 2); // methods
        push_method(&mut bytes, 5, 6, first_code);
        push_method(&mut bytes, 14, 6, second_code);
        push_u16(&mut bytes, 0); // class attributes
        bytes
    }

    fn push_utf8(output: &mut Vec<u8>, value: &str) {
        output.push(1);
        push_u16(output, u16::try_from(value.len()).unwrap());
        output.extend_from_slice(value.as_bytes());
    }

    fn push_class(output: &mut Vec<u8>, name_index: u16) {
        output.push(7);
        push_u16(output, name_index);
    }

    fn push_string(output: &mut Vec<u8>, value_index: u16) {
        output.push(8);
        push_u16(output, value_index);
    }

    fn push_method(output: &mut Vec<u8>, name: u16, descriptor: u16, code: &[u8]) {
        push_u16(output, 0x0009); // public static
        push_u16(output, name);
        push_u16(output, descriptor);
        push_u16(output, 1); // attributes
        push_u16(output, 7); // Code
        let code_attribute_length = 2 + 2 + 4 + code.len() + 2 + 2;
        push_u32(output, u32::try_from(code_attribute_length).unwrap());
        push_u16(output, 1); // max_stack
        push_u16(output, 0); // max_locals
        push_u32(output, u32::try_from(code.len()).unwrap());
        output.extend_from_slice(code);
        push_u16(output, 0); // exceptions
        push_u16(output, 0); // nested attributes
    }

    fn push_u16(output: &mut Vec<u8>, value: u16) {
        output.extend_from_slice(&value.to_be_bytes());
    }

    fn push_u32(output: &mut Vec<u8>, value: u32) {
        output.extend_from_slice(&value.to_be_bytes());
    }
}
