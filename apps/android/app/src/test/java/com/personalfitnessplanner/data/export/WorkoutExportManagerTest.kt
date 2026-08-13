package com.personalfitnessplanner.data.export

import com.google.common.truth.Truth.assertThat
import org.junit.Test

class WorkoutExportManagerTest {
    @Test
    fun escapeCsvCell_NeutralizesSpreadsheetFormulas() {
        assertThat(escapeCsvCell("=HYPERLINK(\"https://example.invalid\")"))
            .isEqualTo("\"'=HYPERLINK(\"\"https://example.invalid\"\")\"")
        assertThat(escapeCsvCell("  +1+1")).isEqualTo("'  +1+1")
        assertThat(escapeCsvCell("-2+3")).isEqualTo("'-2+3")
        assertThat(escapeCsvCell("@SUM(A1:A2)")).isEqualTo("'@SUM(A1:A2)")
    }

    @Test
    fun escapeCsvCell_PreservesOrdinaryTextAndQuotesCsvMetacharacters() {
        assertThat(escapeCsvCell("正常备注")).isEqualTo("正常备注")
        assertThat(escapeCsvCell("深蹲, 第 1 组")).isEqualTo("\"深蹲, 第 1 组\"")
        assertThat(escapeCsvCell("line 1\nline 2")).isEqualTo("\"line 1\nline 2\"")
    }
}
